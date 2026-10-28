using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Cosmos;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel("Cosmos")]
public sealed class CosmosIntegrationTests
{
    [Test]
    public async Task Page_drains_resume_and_delete_across_partitions_without_skipping(CancellationToken cancellationToken)
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        var items = await fixture.Database.GetCosmosContainer("pages", cancellationToken: cancellationToken);
        for (int i = 0; i < 35; i++)
            await items.CreateDocument(new NativeDocument { DocumentId = i.ToString(), PartitionKey = "org-" + i % 3, Score = i }, cancellationToken);
        var repository = fixture.Resolve<CosmosPageRepository>();
        IQueryable<NativeDocument> query = (await repository.BuildQueryable(cancellationToken: cancellationToken)).OrderBy(row => row.Score);
        var first = await query.ToPage(7, cancellationToken: cancellationToken);
        Check(first.ContinuationToken is not null, "Expected multiple pages.");
        // The repository carries the saved page defaults into its one-iterator LINQ drain.
        var resumed = (await repository.BuildPagedQueryable(7, first.ContinuationToken, cancellationToken: cancellationToken)).OrderBy(row => row.Score);
        var seen = new System.Collections.Generic.List<int>();
        await repository.ExecuteOnGetItemsPaged(resumed,
            page => { seen.AddRange(page.Select(row => row.Score)); return ValueTask.CompletedTask; }, cancellationToken);
        Check(seen.SequenceEqual(Enumerable.Range(first.Items.Count, 35 - first.Items.Count)), "Resumed drain skipped or duplicated rows.");
        int deleted = 0;
        await items.QueryPages<NativeDocument>(new QueryDefinition("SELECT * FROM c WHERE c.score_value < 17"), 4, async page =>
        {
            foreach (var row in page) { await items.DeleteItem(row.Id!, cancellationToken); deleted++; }
        }, cancellationToken);
        Check(deleted == 17 && await items.CountItems(cancellationToken) == 18, "Deleting from the active iterator skipped rows.");
        await using var bounded = new CosmosLibrarianDatabase(fixture.Store("pages"), options: new CosmosLibrarianOptions
        {
            QueryRequestOptionsFactory = _ => new QueryRequestOptions { MaxItemCount = 3 }
        });
        var remaining = await bounded.GetCosmosContainer("librarian.test.pages", cancellationToken: cancellationToken);
        Check(await remaining.CountByIndex("score_value", 20, cancellationToken) == 1 &&
            await remaining.CountRangeByIndex("score_value", 17, 34, cancellationToken) == 18 &&
            await remaining.ExistsByIndex("score_value", 20, cancellationToken) &&
            !await remaining.ExistsByIndex("score_value", -1, cancellationToken), "Scalar index queries returned incorrect results.");
        await remaining.DeleteAllItems(cancellationToken);
        Check(await items.CountItems(cancellationToken) == 0, "Bounded deletion left rows behind.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Native_typed_operations_preserve_identity_and_coordinate_concurrent_writers(bool productionSerializer, CancellationToken cancellationToken)
    {
        await using var streams = new Soenneker.Utils.MemoryStream.MemoryStreamUtil();
        await using var fixture = await CosmosPersistenceFixture.Create(productionSerializer ? new Soenneker.Cosmos.Serializer.CosmosSystemTextJsonSerializer(streams) : null);
        var items = await fixture.Database.GetCosmosContainer("native", cancellationToken: cancellationToken);
        var original = new NativeDocument { DocumentId = "one", PartitionKey = "org", Name = "雪😀", Score = 1 };
        await items.CreateDocument(original, cancellationToken);
        var read = await items.ReadDocument<NativeDocument>("org:one", cancellationToken);
        Check(read?.Document.Name == original.Name && read.Document.Score == 1, "Native serializer or identity mapping changed.");
        var first = new NativeDocument { DocumentId = "one", PartitionKey = "org", Score = 2 };
        var second = new NativeDocument { DocumentId = "one", PartitionKey = "org", Score = 3 };
        var competing = await Task.WhenAll(items.ReplaceDocument(first, read!.Version, cancellationToken).AsTask(),
            items.ReplaceDocument(second, read.Version, cancellationToken).AsTask());
        Check(competing.Count(x => x is not null) == 1, "Concurrent writers both succeeded with one version.");
        var winner = competing.Single(x => x is not null)!;
        Check(winner.Version != read.Version, "Native replace did not return a fresh ETag.");
        var patched = await items.PatchDocument<NativeDocument>("org:one", [PatchOperation.Increment("/score_value", 10)], winner.Version, cancellationToken);
        Check(patched?.Document.Score == winner.Document.Score + 10, "Native patch returned the wrong document.");
        Check(await items.PatchDocument<NativeDocument>("org:one", [PatchOperation.Increment("/score_value", 10)], winner.Version, cancellationToken) is null,
            "Native patch accepted a stale version.");
        var many = await items.ReadDocuments<NativeDocument>(["org:one", "org:missing", "org:one"], cancellationToken);
        Check(many[0]?.Score == patched!.Document.Score && many[1] is null && !ReferenceEquals(many[0], many[2]), "Bulk identity or duplication contract failed.");
        await items.DeleteItem("org:one", cancellationToken);
        Check(await items.ReadDocument<NativeDocument>("org:one", cancellationToken) is null, "Deleted native document remained visible.");
    }

    [Test]
    public async Task Cosmos_stores_documents_directly_and_queries_native_fields(CancellationToken cancellationToken)
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("org-a:a", NativeDocumentJson.Create("org-a:a", "{\"score_value\":1,\"name\":\"alpha\"}"), cancellationToken: cancellationToken);
        await items.AddItem("org-a:b", NativeDocumentJson.Create("org-a:b", "{\"score_value\":2,\"name\":\"beta\"}"), cancellationToken: cancellationToken);
        using ResponseMessage stored = await fixture.Store("items").ReadItemStreamAsync("a", new Microsoft.Azure.Cosmos.PartitionKey("org-a"), cancellationToken: cancellationToken);
        stored.EnsureSuccessStatusCode();
        using JsonDocument json = await System.Text.Json.JsonDocument.ParseAsync(stored.Content, cancellationToken: cancellationToken);
        Check(json.RootElement.GetProperty("score_value").GetInt32() == 1 && !json.RootElement.TryGetProperty("body", out _) && !json.RootElement.TryGetProperty("rawJson", out _), "Cosmos document is wrapped.");
        Check(await items.GetItem("org-a:A", cancellationToken: cancellationToken) is null, "Native IDs must be case-sensitive.");
        Check((await items.GetItems(["org-a:b", "missing", "org-a:a", "org-a:b"], cancellationToken: cancellationToken)).Select(x => x is null ? -1 : NativeDocumentJson.Score(x)).SequenceEqual([2, -1, 1, 2]), "Bulk order changed.");
        IQueryable<NativeDocument> query = items.BuildQueryable<NativeDocument>();
        Check(await query.Count(x => x.PartitionKey == "org-a", cancellationToken: cancellationToken) == 2, "Document member query failed.");
        Check(await query.LongCount(cancellationToken: cancellationToken) == 2 && await query.Any(x => x.Score == 2, cancellationToken: cancellationToken) && await query.All(x => x.Score > 0, cancellationToken: cancellationToken), "Native terminals failed.");
        Check(await query.Execute(q => q.Sum(x => x.Score), cancellationToken: cancellationToken) == 3, "Native sum failed.");
        Check(await query.OrderByDescending(x => x.Score).Select(x => x.Name).First(cancellationToken: cancellationToken) == "beta", "Native projection failed.");
        await items.EnsureIndex("score_value", cancellationToken: cancellationToken);
        Check((await items.FindRangeByIndex<NativeDocument>("score_value", 1, 2, true, take: 1, cancellationToken: cancellationToken)).Items[0].Score == 2, "Top-level range index failed.");
        Check((await items.GetAllIds(cancellationToken: cancellationToken)).Order().SequenceEqual(["org-a:a", "org-a:b"]), "Composite IDs were lost.");
        await items.AddItem("org-b:a", NativeDocumentJson.Create("org-b:a", "{\"score_value\":1}"), cancellationToken: cancellationToken);
        Check((await items.FindByIndex<NativeDocument>("score_value", 1, cancellationToken: cancellationToken)).Items.Select(row => row.Id).Order().SequenceEqual(["org-a:a", "org-b:a"]), "Equality query lost a matching partition.");
        Check((await items.FindRangeByIndex<NativeDocument>("score_value", 1, 1, cancellationToken: cancellationToken)).Items.Select(row => row.Id).Order().SequenceEqual(["org-a:a", "org-b:a"]), "Range query lost a matching partition.");
        await items.EnsureIndex("id", cancellationToken: cancellationToken);
        await items.DeleteAllItems(cancellationToken: cancellationToken);
        Check(await items.CountItems(cancellationToken: cancellationToken) == 0, "Clear failed.");
    }
    [Test]
    public async Task Cosmos_native_batches_enforce_versions_and_roll_back_conflicts(CancellationToken cancellationToken)
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        await NativeBatchContract.Exercise(fixture.Database);
    }
    [Test]
    public async Task Cosmos_instances_coordinate_conditional_batches(CancellationToken cancellationToken)
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        await using CosmosLibrarianDatabase second = fixture.CreateDatabase();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", "org-a", cancellationToken: cancellationToken);
        string Json(string id, int score) => NativeDocumentJson.Create(id, $"{{\"score_value\":{score}}}", "org-a");
        await items.AddItem("a", Json("a", 0), cancellationToken: cancellationToken);
        string version = (await items.GetItemWithVersion("a", cancellationToken: cancellationToken))!.Version;
        LibrarianBatch Batch(int score) => new([new LibrarianWrite("items", "a", Json("a", score), version), new LibrarianWrite("items", "b", Json("b", score), CreateOnly: true)]);
        bool[] results = await Task.WhenAll(fixture.Database.Execute(Batch(1), "org-a", cancellationToken: cancellationToken).AsTask(), second.Execute(Batch(2), "org-a", cancellationToken: cancellationToken).AsTask());
        Check(results[0] != results[1], "Competing conditions were not atomic.");
        Check(NativeDocumentJson.Score((await items.GetItem("a", cancellationToken: cancellationToken))!) == NativeDocumentJson.Score((await items.GetItem("b", cancellationToken: cancellationToken))!), "Partial batch publication.");
    }
}
