using System;
using System.Linq;
using System.Threading;
using Soenneker.Librarian.Abstractions.Queries;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Mongo;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class MongoProviderTests
{
    [Test]
    public async Task Mongo_native_contract()
    {
        await using MongoPersistenceFixture fixture = await MongoPersistenceFixture.Create();
        await Exercise(fixture.Database);
    }

    [Test]
    public async Task Mongo_instances_share_immediate_writes_and_atomic_conditions()
    {
        await using MongoPersistenceFixture fixture = await MongoPersistenceFixture.Create();
        await using MongoLibrarianDatabase second = fixture.CreateDatabase();
        ILibrarianContainer firstItems = await fixture.Database.GetContainer("items");
        ILibrarianContainer secondItems = await second.GetContainer("items");
        await firstItems.AddItem("a", "0");
        DocumentProviderAssertions.Check(await secondItems.GetItem("A") == "0", "Write required Save.");
        var firstBatch = new LibrarianBatch([new LibrarianWrite("items", "a", "1"), new LibrarianWrite("other", "a", "1")], [new LibrarianCondition("items", "a", "0")]);
        var secondBatch = new LibrarianBatch([new LibrarianWrite("items", "a", "2"), new LibrarianWrite("other", "a", "2")], [new LibrarianCondition("items", "a", "0")]);
        bool[] outcomes = await Task.WhenAll(fixture.Database.Execute(firstBatch).AsTask(), second.Execute(secondBatch).AsTask());
        DocumentProviderAssertions.Check(outcomes[0] != outcomes[1], "Competing conditional batches both committed or both failed.");
        DocumentProviderAssertions.Check(await firstItems.GetItem("a") == await (await second.GetContainer("other")).GetItem("a"), "Batch partially committed.");
        await second.DisposeAsync();
        DocumentProviderAssertions.Check(await firstItems.GetItem("a") is not null, "Caller-owned client was disposed.");
    }
    private static async Task Exercise(ILibrarianDatabase database)
    {
        ILibrarianContainer items = await database.GetContainer("items");
        await items.AddItem("B", "{\"score_value\":2,\"name\":\"beta\"}");
        await items.AddItem("a", "{\"score_value\":1,\"name\":\"alpha\"}");
        Check(await items.GetItem("b") == "{\"score_value\":2,\"name\":\"beta\"}", "Case insensitive read failed.");
        try { await items.AddItem("b", "duplicate"); throw new Exception("Duplicate accepted."); } catch (InvalidOperationException) { }
        Check(await items.UpdateItem("missing", "value") is null, "Missing update succeeded.");
        Check(await (await database.GetContainer("Items")).CountItems() == 0, "Container case was lost.");
        Check((await items.GetItems(["A", "missing", "a"])).SequenceEqual(new string?[] { "{\"score_value\":1,\"name\":\"alpha\"}", null, "{\"score_value\":1,\"name\":\"alpha\"}" }), "Bulk read changed order.");
        IQueryable<QueryableRow> query = items.BuildQueryable<QueryableRow>();
        Check(await query.Where(x => x.Score >= 1).CountAsync() == 2, "Count failed.");
        Check(query.LongCount() == 2 && query.Any(x => x.Score == 2) && query.All(x => x.Score > 0), "Terminals failed.");
        Check(query.OrderByDescending(x => x.Score).Skip(1).Take(1).Single().Name == "alpha", "Paging failed.");
        Check(query.Where(x => x.Name!.StartsWith("al")).First().Score == 1, "Prefix failed.");
        int[] scores = [1, 3];
        Check(query.Where(x => scores.Contains(x.Score)).Count() == 1, "Membership failed.");
        Check(query.OrderBy(x => x.Score).Take(1).Select(x => x.Name).Single() == "alpha", "Projection failed.");
        Check(query.Where(x => x.Score < 0).Count() == 0, "Empty count failed.");
        Check(query.Skip(1).Count() == 1, "Paged count failed.");
        await items.EnsureIndex("score_value");
        Check(await items.CountRangeByIndex("score_value", 1, 2) == 2, "Range count failed.");
        Check((await items.FindRangeByIndex<QueryableRow>("score_value", 1, 2, true, 0, 1)).Items[0].Score == 2, "Range ordering failed.");
        Check((await items.FindByIndex<QueryableRow>("score_value", 1)).Items[0].Name == "alpha", "Equality page failed.");
        try { await items.UpdateItem("a", "{\"score_value\":[]}"); throw new Exception("Invalid indexed value accepted."); } catch (ArgumentException) { }
        Check(!await database.Execute(new LibrarianBatch([new LibrarianWrite("other", "c", "bad")], [new LibrarianCondition("items", "a", "stale")])), "Stale condition succeeded.");
        string? before = await items.GetItem("a");
        Check(await database.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", "{\"score_value\":3}"), new LibrarianWrite("other", "c", "raw")], [new LibrarianCondition("items", "a", before)])), "Batch failed.");
        Check(await (await database.GetContainer("other")).GetItem("c") == "raw", "Cross-container write lost.");
        Check(await query.CountAsync(x => x.Score == 3) == 1, "Batch did not update the native query body.");
        await database.UnloadContainer("items");
        try { await items.GetItem("a"); throw new Exception("Unloaded handle accepted."); } catch (ObjectDisposedException) { }
        items = await database.GetContainer("items");
        Check(await items.CountByIndex("score_value", 3) == 1, "Persistent index lost.");
        try { await items.DeleteItem("a", new CancellationToken(true)); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        await items.DeleteAllItems();
        Check(await items.CountItems() == 0, "Delete all failed.");
    }

    [Test]
    public async Task Duplicate_json_properties_follow_last_property_semantics()
    {
        await using MongoPersistenceFixture fixture = await MongoPersistenceFixture.Create();
        MongoLibrarianDatabase database = fixture.Database;
        ILibrarianContainer items = await database.GetContainer("items");
        await items.AddItem("a", "{\"nested\":{\"value\":1},\"nested\":2}");
        await items.EnsureIndex("nested.value");
        Check(await items.CountByIndex("nested.value", 1) == 0, "An overwritten JSON property retained an index entry.");
    }

    [Test]
    public async Task Invalid_index_registration_is_atomic_and_null_differs_from_missing()
    {
        await using MongoPersistenceFixture fixture = await MongoPersistenceFixture.Create();
        MongoLibrarianDatabase database = fixture.Database;
        ILibrarianContainer items = await database.GetContainer("items");
        await items.AddItem("bad", "{\"name\":[]}");
        try { await items.EnsureIndex("name"); throw new Exception("Array indexed."); } catch (ArgumentException) { }
        try { await items.CountByIndex("name", null); throw new Exception("Failed index published."); } catch (InvalidOperationException) { }
        await items.DeleteItem("bad");
        await items.AddItem("null", "{\"name\":null}");
        await items.AddItem("missing", "{}");
        await items.EnsureIndex("name");
        Check(await items.CountByIndex("name", null) == 1, "Null and missing were conflated.");
        Check(items.BuildQueryable<QueryableRow>().Count(x => x.Name != null) == 0, "Native Mongo null semantics changed.");
    }

}
