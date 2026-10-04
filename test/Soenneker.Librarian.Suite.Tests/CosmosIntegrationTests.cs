using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Cosmos;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CosmosIntegrationTests
{
    [Test]
    public async Task Cosmos_stores_documents_directly_and_queries_native_fields()
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        ILibrarianContainer items = await fixture.Database.GetContainer("items");
        await items.AddItem("org-a:a", NativeDocumentJson.Create("org-a:a", "{\"score_value\":1,\"name\":\"alpha\"}"));
        await items.AddItem("org-a:b", NativeDocumentJson.Create("org-a:b", "{\"score_value\":2,\"name\":\"beta\"}"));
        using ResponseMessage stored = await fixture.Store("items").ReadItemStreamAsync("a", new Microsoft.Azure.Cosmos.PartitionKey("org-a"));
        stored.EnsureSuccessStatusCode();
        using JsonDocument json = await System.Text.Json.JsonDocument.ParseAsync(stored.Content);
        Check(json.RootElement.GetProperty("score_value").GetInt32() == 1 && !json.RootElement.TryGetProperty("body", out _) && !json.RootElement.TryGetProperty("rawJson", out _), "Cosmos document is wrapped.");
        Check(await items.GetItem("org-a:A") is null, "Native IDs must be case-sensitive.");
        Check((await items.GetItems(["org-a:b", "missing", "org-a:a", "org-a:b"])).Select(x => x is null ? -1 : NativeDocumentJson.Score(x)).SequenceEqual([2, -1, 1, 2]), "Bulk order changed.");
        IQueryable<NativeDocument> query = items.BuildQueryable<NativeDocument>();
        Check(await query.CountAsync(x => x.PartitionKey == "org-a") == 2, "Document member query failed.");
        Check(await query.LongCountAsync() == 2 && await query.AnyAsync(x => x.Score == 2) && await query.AllAsync(x => x.Score > 0), "Native terminals failed.");
        Check(await query.ExecuteAsync(q => q.Sum(x => x.Score)) == 3, "Native sum failed.");
        Check(await query.OrderByDescending(x => x.Score).Select(x => x.Name).FirstAsync() == "beta", "Native projection failed.");
        await items.EnsureIndex("score_value");
        Check((await items.FindRangeByIndex<NativeDocument>("score_value", 1, 2, true, take: 1)).Items[0].Score == 2, "Top-level range index failed.");
        Check((await items.GetAllIds()).Order().SequenceEqual(["org-a:a", "org-a:b"]), "Composite IDs were lost.");
        await items.AddItem("org-b:a", NativeDocumentJson.Create("org-b:a", "{\"score_value\":1}"));
        Check((await items.FindByIndex<NativeDocument>("score_value", 1)).Items.Select(row => row.Id).Order().SequenceEqual(["org-a:a", "org-b:a"]), "Equality query lost a matching partition.");
        Check((await items.FindRangeByIndex<NativeDocument>("score_value", 1, 1)).Items.Select(row => row.Id).Order().SequenceEqual(["org-a:a", "org-b:a"]), "Range query lost a matching partition.");
        await items.EnsureIndex("id");
        await items.DeleteAllItems();
        Check(await items.CountItems() == 0, "Clear failed.");
    }
    [Test]
    public async Task Cosmos_native_batches_enforce_versions_and_roll_back_conflicts()
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        await NativeBatchContract.Exercise(fixture.Database);
    }
    [Test]
    public async Task Cosmos_instances_coordinate_conditional_batches()
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        await using CosmosLibrarianDatabase second = fixture.CreateDatabase();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", "org-a");
        string Json(string id, int score) => NativeDocumentJson.Create(id, $"{{\"score_value\":{score}}}", "org-a");
        await items.AddItem("a", Json("a", 0));
        string version = (await items.GetItemWithVersion("a"))!.Version;
        LibrarianBatch Batch(int score) => new([new LibrarianWrite("items", "a", Json("a", score), version), new LibrarianWrite("items", "b", Json("b", score), CreateOnly: true)]);
        bool[] results = await Task.WhenAll(fixture.Database.Execute(Batch(1), "org-a").AsTask(), second.Execute(Batch(2), "org-a").AsTask());
        Check(results[0] != results[1], "Competing conditions were not atomic.");
        Check(NativeDocumentJson.Score((await items.GetItem("a"))!) == NativeDocumentJson.Score((await items.GetItem("b"))!), "Partial batch publication.");
    }
}
