using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Postgres;

namespace Soenneker.Librarian.Suite.Tests;

public class PostgresTests
{
    [Test]
    public async Task Persistence_case_sensitive_names_and_handle_lifetimes()
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer items = await fixture.Database.GetContainer("Items'); DROP TABLE documents;--");
        const string document = " { \"amount\" : 1.00 } ";
        await items.AddItem("Mixedé", document);
        await items.EnsureIndex("amount");
        await fixture.Database.Save();
        await using PostgresLibrarianDatabase other = fixture.CreateDatabase();
        ILibrarianContainer reloaded = await other.GetContainer("Items'); DROP TABLE documents;--");
        Check(await reloaded.GetItem("MIXEDÉ") == document, "Exact JSON or ID comparison changed.");
        Check(await reloaded.CountByIndex("amount", 1) == 1, "Index did not persist.");
        Check(await (await other.GetContainer("items'); DROP TABLE documents;--")).GetItem("Mixedé") is null, "Container case collided.");
        await reloaded.UpdateItemStrict("mixedé", "{\"amount\":2}");
        Check((await items.GetAllIds()).Single() == "Mixedé", "Update changed the original ID.");
        Check(await fixture.Database.UnloadContainer("Items'); DROP TABLE documents;--"), "Unload failed.");
        try { await items.GetAllIds(); throw new Exception("Disposed handle accepted."); } catch (ObjectDisposedException) { }
        items = await fixture.Database.GetContainer("Items'); DROP TABLE documents;--");
        Check(await items.CountByIndex("amount", 2) == 1, "Unload erased persistent state.");
        await items.DeleteAllItems();
        Check(await items.CountByIndex("amount", 2) == 0, "Delete left stale indexes.");
        await items.AddItem("new", "{\"amount\":3}");
        Check(await items.ExistsByIndex("amount", 3), "DeleteAll removed the index definition.");
    }

    [Test]
    public async Task Sql_queries_page_order_filter_and_aggregate_without_deserializing_other_rows()
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer items = await fixture.Database.GetContainer("items");
        for (var i = 0; i < 20; i++) await items.AddItem(i.ToString("D2"), $"{{\"amount\":{i},\"name\":\"row-{i}\",\"active\":{(i % 2 == 0 ? "true" : "false")}}}");
        IQueryable<PostgresRow> query = items.BuildQueryable<PostgresRow>();
        PostgresRow.Reads.Value = 0;
        Check(query.Count(row => row.Amount >= 5 && (row.Amount < 10 || row.Amount == 15)) == 6, "Boolean SQL filtering failed.");
        Check(query.LongCount(row => !row.Active) == 10 && query.Any(row => row.Amount == 19), "SQL aggregates failed.");
        Check(PostgresRow.Reads.Value == 0, "Aggregate deserialized documents.");
        PostgresRow[] page = query.Where(row => row.Amount >= 5).OrderByDescending(row => row.Amount).Skip(2).Take(3).ToArray();
        Check(page.Select(row => row.Amount).SequenceEqual(new decimal[] { 17, 16, 15 }), "SQL page order failed.");
        Check(PostgresRow.Reads.Value == 3, "Page deserialized extra documents.");
        Check(query.Take(7).Skip(2).Take(3).Count() == 3 && !query.Take(0).Any(), "Composed paging failed.");
        Check(query.Single(row => row.Amount == 12).Amount == 12, "Single failed.");
        Check(query.First(row => row.Amount >= 18).Amount == 18, "First failed.");
        Check(query.FirstOrDefault(row => row.Amount == 99) is null, "Empty FirstOrDefault failed.");
        try { query.Single(); throw new Exception("Single accepted multiple rows."); } catch (InvalidOperationException) { }
        try { query.Where(row => row.Name!.Trim() == "row").ToArray(); throw new Exception("Unsupported query scanned locally."); } catch (NotSupportedException) { }
        Check(query.Take(5).Where(row => row.Active).Count() == 3, "Post-page filter moved before paging.");
        Check(query.Count(row => row.Name!.Length > 2) == 20, "String length failed.");
        IQueryable<PostgresRow> deferred = query.Where(row => row.Amount == 99);
        await items.AddItem("new", "{\"amount\":99}");
        Check(deferred.Any(), "Deferred query retained stale data.");
    }

    [Test]
    public async Task Explicit_indexes_preserve_decimal_null_missing_and_ordinal_semantics()
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer items = await fixture.Database.GetContainer("items");
        await items.AddItem("a", "{\"amount\":-79228162514264337593543950335,\"name\":null}");
        await items.AddItem("b", "{\"amount\":0.0000000000000000000000000001,\"name\":\"😀\"}");
        await items.AddItem("c", "{\"amount\":79228162514264337593543950335,\"name\":\"\\uE000\"}");
        await items.AddItem("d", "{}");
        try { await items.CountByIndex("name", null); throw new Exception("Missing index accepted."); } catch (InvalidOperationException) { }
        await items.EnsureIndex("amount");
        await items.EnsureIndex("name");
        Check(await items.CountByIndex("name", null) == 1, "Missing and null were conflated.");
        var range = await items.FindRangeByIndex<PostgresRow>("amount", decimal.MinValue, decimal.MaxValue);
        Check(range.Items.Select(row => row.Amount).SequenceEqual(new[] { decimal.MinValue, 0.0000000000000000000000000001m, decimal.MaxValue }), "Decimal precision lost.");
        var strings = await items.FindRangeByIndex<PostgresRow>("name");
        Check(strings.Items.Select(row => row.Name).SequenceEqual(new string?[] { null, "😀", "\uE000" }), "Ordinal UTF-16 ordering changed.");
        Check(items.BuildQueryable<PostgresRow>().Count(row => row.Name != null) == 3, "Missing-property complement changed.");
    }

    [Test]
    public async Task Independent_instances_compete_and_rollback_validation_failures()
    {
        await using var fixture = new PostgresPersistenceFixture();
        await using PostgresLibrarianDatabase other = fixture.CreateDatabase();
        ILibrarianContainer jobs = await fixture.Database.GetContainer("jobs");
        await jobs.AddItem("job", "queued");
        var batch = new LibrarianBatch([new("jobs", "job", "running"), new("leases", "job", "owner")], [new("jobs", "job", "queued")]);
        bool[] results = await Task.WhenAll(fixture.Database.Execute(batch).AsTask(), other.Execute(batch).AsTask());
        Check(results.Count(result => result) == 1, "Both instances committed the same claim.");
        ILibrarianContainer indexed = await fixture.Database.GetContainer("indexed");
        await indexed.AddItem("a", "{\"amount\":1}");
        await indexed.EnsureIndex("amount");
        try
        {
            await other.Execute(new([new("jobs", "job", "leaked"), new("indexed", "a", "{\"amount\":{}}") ]));
            throw new Exception("Invalid index accepted.");
        }
        catch (ArgumentException) { }
        Check(await jobs.GetItem("job") == "running" && await indexed.CountByIndex("amount", 1) == 1, "Failed transaction leaked changes.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await indexed.DeleteAllItems(cancelled.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        Check(await indexed.CountByIndex("amount", 1) == 1, "Cancellation deleted data.");
        await other.DisposeAsync();
        await using NpgsqlCommand command = fixture.Source.CreateCommand("SELECT 1");
        Check((int)(await command.ExecuteScalarAsync())! == 1, "Provider disposed caller-owned source.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public sealed class PostgresRow
{
    public static readonly ReadCounter Reads = new();
    private decimal _amount;
    public decimal Amount { get => _amount; set { _amount = value; Reads.Value++; } }
    public string? Name { get; set; }
    public bool Active { get; set; }

    public sealed class ReadCounter
    {
        private readonly AsyncLocal<int[]> _scope = new();
        public int Value
        {
            get => _scope.Value?[0] ?? 0;
            set
            {
                if (value == 0 || _scope.Value is null) _scope.Value = [value];
                else _scope.Value[0] = value;
            }
        }
    }
}
