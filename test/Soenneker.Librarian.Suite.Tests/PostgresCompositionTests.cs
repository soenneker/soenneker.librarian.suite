using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Suite.Tests;

public class PostgresCompositionTests
{
    [Test]
    public async Task Nested_pages_filters_orders_and_projection_aliases_keep_operator_order()
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("composition");
        for (var i = 0; i < 20; i++) await container.AddItem(i.ToString("D2"), $"{{\"amount\":{i},\"name\":\"row-{i:D2}\",\"active\":true}}");
        IQueryable<PostgresRow> query = container.BuildQueryable<PostgresRow>();
        Check(await query.OrderByDescending(row => row.Amount).Take(5).Where(row => row.Amount < 18).CountAsync() == 3,
            "Predicate moved before the inner page.");
        decimal[] actual = await query.OrderByDescending(row => row.Amount).Take(8)
            .Where(row => row.Amount < 18).OrderBy(row => row.Amount).Skip(1).Take(4)
            .Where(row => row.Amount > 13).Select(row => row.Amount).ToArrayAsync();
        Check(actual.SequenceEqual(new decimal[] { 14, 15, 16 }), "Nested stages changed ordering or membership.");
        var projected = query.Select(row => new { Cost = row.Amount, Label = row.Name })
            .Where(row => row.Cost >= 10).OrderByDescending(row => row.Cost).Take(4)
            .Where(row => row.Cost < 18).Select(row => new { row.Label, Price = row.Cost });
        Check((await projected.ToListAsync()).Select(row => row.Price).SequenceEqual(new decimal[] { 17, 16 }), "Projection aliases were not resolved.");
        Check(await query.OrderBy(row => row.Amount).Take(5).AllAsync(row => row.Amount < 5), "All ignored the page.");
        Check((await query.Take(3).FirstAsync(row => row.Amount > 0)).Amount == 1, "First predicate moved before paging.");
        Check(await query.OrderBy(row => row.Amount).Skip(10).Take(3).Where(row => row.Amount > 10)
            .ExecuteAsync(q => q.Sum(row => row.Amount)) == 23, "Aggregate lost nested paging.");
        try { await query.Select(row => new TransformedDto { Value = row.Amount }).Where(row => row.Value > 10).ToListAsync(); throw new Exception("Custom DTO getter mapped to raw field."); }
        catch (NotSupportedException) { }
    }

    [Test]
    public async Task Scalar_distinct_and_computed_expressions_stay_on_the_server()
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("computed");
        for (var i = 0; i < 6; i++) await container.AddItem(i.ToString(), $"{{\"amount\":{i / 2},\"name\":\"😀x\",\"active\":true}}");
        IQueryable<PostgresRow> query = container.BuildQueryable<PostgresRow>();
        PostgresRow.Reads.Value = 0;
        decimal[] distinct = await query.OrderByDescending(row => row.Amount).Select(row => row.Amount).Distinct().ToArrayAsync();
        Check(distinct.SequenceEqual(new decimal[] { 2, 1, 0 }), "Scalar Distinct did not retain first occurrences.");
        Check(await query.Select(row => row.Amount).Distinct().Where(value => value > 0).CountAsync() == 2, "Distinct composition failed.");
        Check(await query.Take(3).Select(row => row.Amount).Distinct().CountAsync() == 2, "Distinct moved before paging.");
        Check(await query.Select(row => row.Amount).Distinct().ExecuteAsync(q => q.Sum()) == 3, "Distinct aggregate failed.");
        var values = await query.Where(row => row.Amount * 2 + 1 >= 3).Select(row => new { Price = row.Amount * 2 + 1, Length = row.Name!.Length }).ToListAsync();
        Check(values.Select(row => row.Price).SequenceEqual(new decimal[] { 3, 3, 5, 5 }) && values.All(row => row.Length == 3), "Arithmetic or UTF-16 length failed.");
        Check(await query.CountAsync(row => row.Name!.Length == 3) == 6, "Computed predicate failed.");
        Check(await query.CountAsync(row => row.Amount >= row.Amount) == 6, "Property comparison failed.");
        Check(await query.ExecuteAsync(q => q.Sum(row => row.Amount * 2)) == 12, "Computed aggregate failed.");
        Check(await query.Select(row => (row.Amount + 1) / 3m).FirstAsync() == 1m / 3m, "Decimal division lost scale.");
        Check(await query.Take(3).ExecuteAsync(q => q.Average(row => row.Amount)) == 1m / 3m, "Decimal average lost scale.");
        Check(PostgresRow.Reads.Value == 0, "Computed query deserialized full documents.");
    }

    [Test]
    public async Task Cancellation_interrupts_index_lock_wait_without_blocking_the_caller()
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("cancel");
        await container.AddItem("one", "{\"amount\":1}");
        await using NpgsqlConnection connection = await fixture.Source.OpenConnectionAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using NpgsqlCommand command = new("SELECT database_key FROM public.librarian_postgres_databases WHERE database_key=$1 FOR UPDATE", connection, transaction);
        command.Parameters.AddWithValue(string.Concat(fixture.Key.Select(character => ((int)character).ToString("X4"))));
        await command.ExecuteNonQueryAsync();
        using var cancellation = new CancellationTokenSource();
        Task operation = container.BuildQueryable<PostgresRow>().Where(row => row.Amount == 1).ToListAsync(cancellation.Token).AsTask();
        // A synchronous implementation would never return above while this connection owns the write lock.
        cancellation.Cancel();
        try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        await transaction.RollbackAsync();
        Check(await container.BuildQueryable<PostgresRow>().CountAsync(row => row.Amount == 1) == 1, "Cancellation damaged index retry.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public sealed class TransformedDto
    {
        private decimal _value;
        public decimal Value { get => _value * 2; set => _value = value; }
    }
}
