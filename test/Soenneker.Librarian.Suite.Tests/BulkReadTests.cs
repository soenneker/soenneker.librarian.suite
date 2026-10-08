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
    public async ValueTask Reads_preserve_order_duplicates_missing_values_and_case_insensitive_ids(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer items = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        Check(await items.CountItems(cancellationToken: cancellationToken) == 0 && (await items.GetItems([], cancellationToken: cancellationToken)).Length == 0);
        await items.AddItem("one", "", cancellationToken: cancellationToken);
        await items.AddItem("two", "second", cancellationToken: cancellationToken);
        Check((await items.GetItems(["TWO", "missing", "one", "two"], cancellationToken: cancellationToken)).SequenceEqual(new string?[] { "second", null, "", "second" }));
        Check(await items.CountItems(cancellationToken: cancellationToken) == 2);
        await items.DeleteItem("ONE", cancellationToken: cancellationToken);
        Check(await items.CountItems(cancellationToken: cancellationToken) == 1);
        try { await items.GetItems(["two"], new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        try { await items.GetItems(["two", null!], cancellationToken: cancellationToken); throw new Exception("Null ID accepted."); }
        catch (ArgumentNullException) { }
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async ValueTask Bulk_reads_never_observe_partial_batches(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer items = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await fixture.Database.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", "0"), new LibrarianWrite("items", "b", "0")]), cancellationToken: cancellationToken);
        Task writer = Task.Run(async () =>
        {
            for (var i = 1; i <= 30; i++)
                await fixture.Database.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", i.ToString()), new LibrarianWrite("items", "b", i.ToString())]), cancellationToken: cancellationToken);
        }, cancellationToken: cancellationToken);
        for (var i = 0; i < 50; i++)
        {
            string?[] values = await items.GetItems(["b", "a", "B"], cancellationToken: cancellationToken);
            Check(values[0] == values[1] && values[1] == values[2]);
        }
        await writer;
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async ValueTask Range_counts_handle_boundaries_missing_fields_and_index_updates(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer items = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await fixture.Database.Execute(new LibrarianBatch([
            new LibrarianWrite("items", "a", "{\"score\":-1}"), new LibrarianWrite("items", "b", "{\"score\":0}"),
            new LibrarianWrite("items", "c", "{\"score\":0}"), new LibrarianWrite("items", "d", "{\"score\":2}"),
            new LibrarianWrite("items", "null", "{\"score\":null}"), new LibrarianWrite("items", "missing", "{}") ]), cancellationToken: cancellationToken);
        await items.EnsureIndex("score", cancellationToken: cancellationToken);
        Check(await items.CountRangeByIndex("score", cancellationToken: cancellationToken) == 5);
        Check(await items.CountRangeByIndex("score", -1, 0, cancellationToken: cancellationToken) == 3);
        Check(await items.CountRangeByIndex("score", minimum: 0, cancellationToken: cancellationToken) == 3);
        Check(await items.CountRangeByIndex("score", maximum: -1, cancellationToken: cancellationToken) == 2);
        Check(await items.CountRangeByIndex("score", 1, 1, cancellationToken: cancellationToken) == 0);
        await fixture.Database.Execute(new LibrarianBatch([new LibrarianWrite("items", "c", "{\"score\":3}"), new LibrarianWrite("items", "b", null)]), cancellationToken: cancellationToken);
        Check(await items.CountRangeByIndex("score", -1, 0, cancellationToken: cancellationToken) == 1 && await items.CountItems(cancellationToken: cancellationToken) == 5);
        try { await items.CountRangeByIndex("score", 1, -1, cancellationToken: cancellationToken); throw new Exception("Reversed bounds accepted."); }
        catch (ArgumentException) { }
        try { await items.CountRangeByIndex("score", 1, "a", cancellationToken: cancellationToken); throw new Exception("Mixed bounds accepted."); }
        catch (ArgumentException) { }
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async ValueTask Read_only_conditions_distinguish_empty_missing_and_stale_documents(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        await (await fixture.Database.GetContainer("one", cancellationToken: cancellationToken)).AddItem("empty", "", cancellationToken: cancellationToken);
        await (await fixture.Database.GetContainer("two", cancellationToken: cancellationToken)).AddItem("value", "old", cancellationToken: cancellationToken);
        LibrarianCondition[] conditions = [new("one", "EMPTY", ""), new("one", "missing", null), new("two", "value", "old")];
        Check(await fixture.Database.Execute(new LibrarianBatch([], conditions), cancellationToken: cancellationToken));
        await (await fixture.Database.GetContainer("two", cancellationToken: cancellationToken)).UpdateItem("value", "new", cancellationToken: cancellationToken);
        Check(!await fixture.Database.Execute(new LibrarianBatch([], conditions), cancellationToken: cancellationToken));
        Check(!await fixture.Database.Execute(new LibrarianBatch([], [new LibrarianCondition("one", "empty", null)]), cancellationToken: cancellationToken));
        Check(!await fixture.Database.Execute(new LibrarianBatch([], [new LibrarianCondition("one", "missing", "")]), cancellationToken: cancellationToken));
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Bulk read or cardinality contract violated.");
    }
}
