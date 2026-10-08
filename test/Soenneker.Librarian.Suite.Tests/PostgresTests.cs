using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Postgres;

namespace Soenneker.Librarian.Suite.Tests;

public class PostgresTests
{
    [Test]
    public async ValueTask Persistence_case_sensitive_names_and_handle_lifetimes(CancellationToken cancellationToken)
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer items = await fixture.Database.GetContainer("Items'); DROP TABLE documents;--", cancellationToken: cancellationToken);
        const string document = " { \"amount\" : 1.00 } ";
        await items.AddItem("Mixedé", document, cancellationToken: cancellationToken);
        await items.EnsureIndex("amount", cancellationToken: cancellationToken);
        await fixture.Database.Save(cancellationToken: cancellationToken);
        await using PostgresLibrarianDatabase other = fixture.CreateDatabase();
        ILibrarianContainer reloaded = await other.GetContainer("Items'); DROP TABLE documents;--", cancellationToken: cancellationToken);
        Check(await reloaded.GetItem("MIXEDÉ", cancellationToken: cancellationToken) == document, "Exact JSON or ID comparison changed.");
        Check(await reloaded.CountByIndex("amount", 1, cancellationToken: cancellationToken) == 1, "Index did not persist.");
        Check(await (await other.GetContainer("items'); DROP TABLE documents;--", cancellationToken: cancellationToken)).GetItem("Mixedé", cancellationToken: cancellationToken) is null, "Container case collided.");
        await reloaded.UpdateItemStrict("mixedé", "{\"amount\":2}", cancellationToken: cancellationToken);
        Check((await items.GetAllIds(cancellationToken: cancellationToken)).Single() == "Mixedé", "Update changed the original ID.");
        Check(await fixture.Database.UnloadContainer("Items'); DROP TABLE documents;--", cancellationToken: cancellationToken), "Unload failed.");
        try { await items.GetAllIds(cancellationToken: cancellationToken); throw new Exception("Disposed handle accepted."); } catch (ObjectDisposedException) { }
        items = await fixture.Database.GetContainer("Items'); DROP TABLE documents;--", cancellationToken: cancellationToken);
        Check(await items.CountByIndex("amount", 2, cancellationToken: cancellationToken) == 1, "Unload erased persistent state.");
        await items.DeleteAllItems(cancellationToken: cancellationToken);
        Check(await items.CountByIndex("amount", 2, cancellationToken: cancellationToken) == 0, "Delete left stale indexes.");
        await items.AddItem("new", "{\"amount\":3}", cancellationToken: cancellationToken);
        Check(await items.ExistsByIndex("amount", 3, cancellationToken: cancellationToken), "DeleteAll removed the index definition.");
    }

    [Test]
    public async ValueTask Sql_queries_page_order_filter_and_aggregate_without_deserializing_other_rows(CancellationToken cancellationToken)
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        for (var i = 0; i < 20; i++) await items.AddItem(i.ToString("D2"), $"{{\"amount\":{i},\"name\":\"row-{i}\",\"active\":{(i % 2 == 0 ? "true" : "false")}}}", cancellationToken: cancellationToken);
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
        await items.AddItem("new", "{\"amount\":99}", cancellationToken: cancellationToken);
        Check(deferred.Any(), "Deferred query retained stale data.");
    }

    [Test]
    public async ValueTask Explicit_indexes_preserve_decimal_null_missing_and_ordinal_semantics(CancellationToken cancellationToken)
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("a", "{\"amount\":-79228162514264337593543950335,\"name\":null}", cancellationToken: cancellationToken);
        await items.AddItem("b", "{\"amount\":0.0000000000000000000000000001,\"name\":\"😀\"}", cancellationToken: cancellationToken);
        await items.AddItem("c", "{\"amount\":79228162514264337593543950335,\"name\":\"\\uE000\"}", cancellationToken: cancellationToken);
        await items.AddItem("d", "{}", cancellationToken: cancellationToken);
        try { await items.CountByIndex("name", null, cancellationToken: cancellationToken); throw new Exception("Missing index accepted."); } catch (InvalidOperationException) { }
        await items.EnsureIndex("amount", cancellationToken: cancellationToken);
        await items.EnsureIndex("name", cancellationToken: cancellationToken);
        Check(await items.CountByIndex("name", null, cancellationToken: cancellationToken) == 1, "Missing and null were conflated.");
        LibrarianQueryResult<PostgresRow> range = await items.FindRangeByIndex<PostgresRow>("amount", decimal.MinValue, decimal.MaxValue, cancellationToken: cancellationToken);
        Check(range.Items.Select(row => row.Amount).SequenceEqual(new[] { decimal.MinValue, 0.0000000000000000000000000001m, decimal.MaxValue }), "Decimal precision lost.");
        LibrarianQueryResult<PostgresRow> strings = await items.FindRangeByIndex<PostgresRow>("name", cancellationToken: cancellationToken);
        Check(strings.Items.Select(row => row.Name).SequenceEqual(new string?[] { null, "😀", "\uE000" }), "Ordinal UTF-16 ordering changed.");
        Check(items.BuildQueryable<PostgresRow>().Count(row => row.Name != null) == 3, "Missing-property complement changed.");
    }

    [Test]
    public async ValueTask Independent_instances_compete_and_rollback_validation_failures(CancellationToken cancellationToken)
    {
        await using var fixture = new PostgresPersistenceFixture();
        await using PostgresLibrarianDatabase other = fixture.CreateDatabase();
        ILibrarianContainer jobs = await fixture.Database.GetContainer("jobs", cancellationToken: cancellationToken);
        await jobs.AddItem("job", "queued", cancellationToken: cancellationToken);
        var batch = new LibrarianBatch([new LibrarianWrite("jobs", "job", "running"), new LibrarianWrite("leases", "job", "owner")], [new LibrarianCondition("jobs", "job", "queued")]);
        bool[] results = await Task.WhenAll(fixture.Database.Execute(batch, cancellationToken: cancellationToken).AsTask(), other.Execute(batch, cancellationToken: cancellationToken).AsTask());
        Check(results.Count(result => result) == 1, "Both instances committed the same claim.");
        ILibrarianContainer indexed = await fixture.Database.GetContainer("indexed", cancellationToken: cancellationToken);
        await indexed.AddItem("a", "{\"amount\":1}", cancellationToken: cancellationToken);
        await indexed.EnsureIndex("amount", cancellationToken: cancellationToken);
        try
        {
            await other.Execute(new LibrarianBatch([new LibrarianWrite("jobs", "job", "leaked"), new LibrarianWrite("indexed", "a", "{\"amount\":{}}") ]), cancellationToken: cancellationToken);
            throw new Exception("Invalid index accepted.");
        }
        catch (ArgumentException) { }
        Check(await jobs.GetItem("job", cancellationToken: cancellationToken) == "running" && await indexed.CountByIndex("amount", 1, cancellationToken: cancellationToken) == 1, "Failed transaction leaked changes.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try { await indexed.DeleteAllItems(cancelled.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        Check(await indexed.CountByIndex("amount", 1, cancellationToken: cancellationToken) == 1, "Cancellation deleted data.");
        await other.DisposeAsync();
        await using NpgsqlCommand command = fixture.Source.CreateCommand("SELECT 1");
        Check((int)(await command.ExecuteScalarAsync(cancellationToken: cancellationToken))! == 1, "Provider disposed caller-owned source.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
