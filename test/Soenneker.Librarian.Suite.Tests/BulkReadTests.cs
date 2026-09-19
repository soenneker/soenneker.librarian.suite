using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel("Redis")]
public class BulkReadTests
{
    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async Task Reads_preserve_order_duplicates_missing_values_and_case_insensitive_ids(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer items = await fixture.Database.GetContainer("items");
        Check(await items.CountItems() == 0 && (await items.GetItems([])).Length == 0);
        await items.AddItem("one", "");
        await items.AddItem("two", "second");
        Check((await items.GetItems(["TWO", "missing", "one", "two"])).SequenceEqual(new string?[] { "second", null, "", "second" }));
        Check(await items.CountItems() == 2);
        await items.DeleteItem("ONE");
        Check(await items.CountItems() == 1);
        try { await items.GetItems(["two"], new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        try { await items.GetItems(["two", null!]); throw new Exception("Null ID accepted."); }
        catch (ArgumentNullException) { }
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async Task Bulk_reads_never_observe_partial_batches(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer items = await fixture.Database.GetContainer("items");
        await fixture.Database.Execute(new([new("items", "a", "0"), new("items", "b", "0")]));
        Task writer = Task.Run(async () =>
        {
            for (int i = 1; i <= 30; i++)
                await fixture.Database.Execute(new([new("items", "a", i.ToString()), new("items", "b", i.ToString())]));
        });
        for (int i = 0; i < 50; i++)
        {
            string?[] values = await items.GetItems(["b", "a", "B"]);
            Check(values[0] == values[1] && values[1] == values[2]);
        }
        await writer;
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async Task Range_counts_handle_boundaries_missing_fields_and_index_updates(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer items = await fixture.Database.GetContainer("items");
        await fixture.Database.Execute(new([
            new("items", "a", "{\"score\":-1}"), new("items", "b", "{\"score\":0}"),
            new("items", "c", "{\"score\":0}"), new("items", "d", "{\"score\":2}"),
            new("items", "null", "{\"score\":null}"), new("items", "missing", "{}") ]));
        await items.EnsureIndex("score");
        Check(await items.CountRangeByIndex("score") == 5);
        Check(await items.CountRangeByIndex("score", -1, 0) == 3);
        Check(await items.CountRangeByIndex("score", minimum: 0) == 3);
        Check(await items.CountRangeByIndex("score", maximum: -1) == 2);
        Check(await items.CountRangeByIndex("score", 1, 1) == 0);
        await fixture.Database.Execute(new([new("items", "c", "{\"score\":3}"), new("items", "b", null)]));
        Check(await items.CountRangeByIndex("score", -1, 0) == 1 && await items.CountItems() == 5);
        try { await items.CountRangeByIndex("score", 1, -1); throw new Exception("Reversed bounds accepted."); }
        catch (ArgumentException) { }
        try { await items.CountRangeByIndex("score", 1, "a"); throw new Exception("Mixed bounds accepted."); }
        catch (ArgumentException) { }
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async Task Read_only_conditions_distinguish_empty_missing_and_stale_documents(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        await (await fixture.Database.GetContainer("one")).AddItem("empty", "");
        await (await fixture.Database.GetContainer("two")).AddItem("value", "old");
        LibrarianCondition[] conditions = [new("one", "EMPTY", ""), new("one", "missing", null), new("two", "value", "old")];
        Check(await fixture.Database.Execute(new([], conditions)));
        await (await fixture.Database.GetContainer("two")).UpdateItem("value", "new");
        Check(!await fixture.Database.Execute(new([], conditions)));
        Check(!await fixture.Database.Execute(new([], [new("one", "empty", null)])));
        Check(!await fixture.Database.Execute(new([], [new("one", "missing", "")])));
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Bulk read or cardinality contract violated.");
    }
}
