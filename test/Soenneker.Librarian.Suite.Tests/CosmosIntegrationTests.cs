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

public sealed class CosmosIntegrationTests
{
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
        Check(await query.CountAsync(x => x.PartitionKey == "org-a", cancellationToken: cancellationToken) == 2, "Document member query failed.");
        Check(await query.LongCountAsync(cancellationToken: cancellationToken) == 2 && await query.AnyAsync(x => x.Score == 2, cancellationToken: cancellationToken) && await query.AllAsync(x => x.Score > 0, cancellationToken: cancellationToken), "Native terminals failed.");
        Check(await query.ExecuteAsync(q => q.Sum(x => x.Score), cancellationToken: cancellationToken) == 3, "Native sum failed.");
        Check(await query.OrderByDescending(x => x.Score).Select(x => x.Name).FirstAsync(cancellationToken: cancellationToken) == "beta", "Native projection failed.");
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
