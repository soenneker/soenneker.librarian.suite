using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Utils.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using System.Text.Json;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

public class PostgresExpandedQueryTests
{
    [Test]
    public async ValueTask Multi_key_ordering_and_projections_fetch_only_selected_fields(CancellationToken cancellationToken)
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("projection", cancellationToken: cancellationToken);
        await container.AddItem("a", "{\"name\":\"B\",\"amount\":1,\"active\":true}", cancellationToken: cancellationToken);
        await container.AddItem("b", "{\"name\":\"A\",\"amount\":1,\"active\":false}", cancellationToken: cancellationToken);
        await container.AddItem("c", "{\"name\":\"C\",\"amount\":2,\"active\":true}", cancellationToken: cancellationToken);
        await container.AddItem("d", "{\"name\":\"D\",\"amount\":2,\"active\":false}", cancellationToken: cancellationToken);
        IQueryable<PostgresRow> query = container.BuildQueryable<PostgresRow>();
        PostgresRow.Reads.Value = 0;
        var page = (await query.OrderBy(row => row.Amount).ThenByDescending(row => row.Name)
            .Skip(1).Take(2).Select(row => new { row.Name, row.Active }).ToArrayAsync(cancellationToken));
        Check(page.Select(row => row.Name).SequenceEqual(new[] { "A", "D" }), "Multiple ordering keys or page boundaries changed.");
        Check(PostgresRow.Reads.Value == 0, "Projection deserialized the source document.");
        string?[] scalar = (await query.OrderByDescending(row => row.Amount).ThenBy(row => row.Name).Select(row => row.Name).Skip(1).Take(2).ToArrayAsync(cancellationToken));
        Check(scalar.SequenceEqual(new[] { "D", "A" }), "Paging after projection changed ordering.");
        ProjectionDto dto = (await query.ExecuteAsync(q => q.Where(row => row.Name == "B").Select(row => new ProjectionDto { Label = row.Name, Value = row.Amount }).Single(), cancellationToken));
        Check(dto.Label == "B" && dto.Value == 1, "Member initializer projection failed.");
        Check((await query.ExecuteAsync(q => q.Where(row => row.Name == "C").Select(row => new ProjectionRecord(row.Name!, row.Amount)).First(), cancellationToken)).Amount == 2,
            "Constructor projection failed.");
        Check((await query.ExecuteAsync(q => q.Select(row => row.Name).Count(), cancellationToken)) == 4, "Projection aggregate failed.");
        Check((await query.ExecuteAsync(q => q.Where(row => row.Amount == 9).Select(row => row.Amount).FirstOrDefault(), cancellationToken)) == 0, "Scalar default was incorrect.");
        Check((await query.ExecuteAsync(q => q.Where(row => row.Amount == 9).Select(row => new { row.Name }).FirstOrDefault(), cancellationToken)) is null, "Reference default was incorrect.");
        Check(PostgresRow.Reads.Value == 0, "Scalar projection fetched full objects.");
        Check((await query.ExecuteAsync(q => q.Select(row => row.Amount + 1).First(), cancellationToken)) == 2, "Computed projection failed.");
        Check((await query.ExecuteAsync(q => q.Select(row => row.Name).Where(name => name == "A").Single(), cancellationToken)) == "A", "Projected filter failed.");
        await Reject(async () => { _ = (await query.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArrayAsync(cancellationToken)); });
    }

    [Test]
    public async ValueTask String_search_preserves_literals_and_utf16_boundaries(CancellationToken cancellationToken)
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("strings", cancellationToken: cancellationToken);
        string[] names = ["A%_\\B", "AB", "prefix-tail", "prefix-😀-tail", "' OR 1=1 --", "", "Prefix-tail"];
        for (var i = 0; i < names.Length; i++) await container.AddItem(i.ToString(), JsonUtil.Serialize(new { name = names[i] })!, cancellationToken: cancellationToken);
        await container.AddItem("null", "{\"name\":null}", cancellationToken: cancellationToken);
        await container.AddItem("missing", "{}", cancellationToken: cancellationToken);
        IQueryable<PostgresRow> query = container.BuildQueryable<PostgresRow>();
        foreach (string term in new[] { "prefix", "tail", "%_\\", "😀", "' OR 1=1 --", "", "\u1004" })
        {
            Check((await query.ExecuteAsync(q => q.Count(row => row.Name!.StartsWith(term)), cancellationToken)) == names.Count(name => name.StartsWith(term, StringComparison.Ordinal)), "Prefix mismatch.");
            Check((await query.ExecuteAsync(q => q.Count(row => row.Name!.EndsWith(term, StringComparison.Ordinal)), cancellationToken)) == names.Count(name => name.EndsWith(term, StringComparison.Ordinal)), "Suffix mismatch.");
            Check((await query.ExecuteAsync(q => q.Count(row => row.Name!.Contains(term)), cancellationToken)) == names.Count(name => name.Contains(term, StringComparison.Ordinal)), "Substring mismatch.");
        }
        Check((await query.ExecuteAsync(q => q.Count(row => row.Name!.StartsWith("prefix") && row.Name.EndsWith("tail")), cancellationToken)) == 2, "Composed string predicates failed.");
        await Reject(async () => { _ = (await query.ExecuteAsync(q => q.Count(row => row.Name!.Contains("PREFIX", StringComparison.OrdinalIgnoreCase)), cancellationToken)); });
        await Reject(async () => { _ = (await query.ExecuteAsync(q => q.Count(row => row.Name!.StartsWith("prefix", StringComparison.CurrentCulture)), cancellationToken)); });
    }

    [Test]
    public async ValueTask Collection_membership_all_and_boolean_constants_execute_on_server(CancellationToken cancellationToken)
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("membership", cancellationToken: cancellationToken);
        await container.AddItem("a", "{\"name\":\"A\",\"amount\":1}", cancellationToken: cancellationToken);
        await container.AddItem("b", "{\"name\":\"B\",\"amount\":2}", cancellationToken: cancellationToken);
        await container.AddItem("c", "{\"name\":null,\"amount\":3}", cancellationToken: cancellationToken);
        await container.AddItem("d", "{\"amount\":4}", cancellationToken: cancellationToken);
        IQueryable<PostgresRow> query = container.BuildQueryable<PostgresRow>();
        string?[] names = ["A", null, "A"];
        var numbers = new List<decimal> { 2, 3 };
        var set = new HashSet<string> { "A", "B" };
        Check((await query.ExecuteAsync(q => q.Count(row => names.Contains(row.Name)), cancellationToken)) == 2, "IN did not preserve explicit null or duplicate semantics.");
        Check((await query.ExecuteAsync(q => q.Count(row => numbers.Contains(row.Amount)), cancellationToken)) == 2, "List membership failed.");
        Check((await query.ExecuteAsync(q => q.Count(row => set.Contains(row.Name!)), cancellationToken)) == 2, "Set membership failed.");
        Check((await query.ExecuteAsync(q => q.Count(row => new[] { "A", "B" }.Contains(row.Name)), cancellationToken)) == 2, "Inline array membership failed.");
        decimal[] empty = [];
        Check(!(await query.ExecuteAsync(q => q.Any(row => empty.Contains(row.Amount)), cancellationToken)), "Empty membership matched rows.");
        Check((await query.ExecuteAsync(q => q.All(row => row.Amount >= 1), cancellationToken)) && !(await query.ExecuteAsync(q => q.All(row => row.Amount > 1), cancellationToken)), "All failed.");
        Check((await query.ExecuteAsync(q => q.Where(row => row.Amount > 99).All(row => row.Amount < 0), cancellationToken)), "Empty All must be true.");
        Check((await query.ExecuteAsync(q => q.Count(row => true), cancellationToken)) == 4 && !(await query.ExecuteAsync(q => q.Any(row => false), cancellationToken)), "Constant predicate failed.");
        PostgresRow.Reads.Value = 0;
        Check((await query.ExecuteAsync(q => q.All(row => !empty.Contains(row.Amount)), cancellationToken)), "Negated membership failed.");
        Check(PostgresRow.Reads.Value == 0, "All deserialized documents.");
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a" };
        await Reject(async () => { _ = (await query.ExecuteAsync(q => q.Count(row => insensitive.Contains(row.Name!)), cancellationToken)); });
    }

    [Test]
    public async ValueTask Numeric_aggregates_preserve_types_paging_and_empty_results(CancellationToken cancellationToken)
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("aggregates", cancellationToken: cancellationToken);
        NumericRow[] rows = [new() { Number = 1, Amount = 0.1m, Optional = 2, Fraction = 1.25 },
            new() { Number = 2, Amount = 0.2m, Optional = null, Fraction = 2.5 },
            new() { Number = 3, Amount = 0.3m, Optional = 4, Fraction = 3.75 }];
        for (var i = 0; i < rows.Length; i++) await container.AddItem(i.ToString(), JsonUtil.Serialize(rows[i], TestJsonContext.Default.PostgresExpandedQueryTestsNumericRow), cancellationToken: cancellationToken);
        IQueryable<NumericRow> query = container.BuildQueryable<NumericRow>();
        Check((await query.ExecuteAsync(q => q.Sum(row => row.Number), cancellationToken)) == 6, "Integer Sum failed.");
        Check((await query.ExecuteAsync(q => q.Average(row => row.Number), cancellationToken)) == 2d, "Integer Average return type failed.");
        Check((await query.ExecuteAsync(q => q.Sum(row => row.Amount), cancellationToken)) == 0.6m && (await query.ExecuteAsync(q => q.Average(row => row.Amount), cancellationToken)) == 0.2m, "Decimal aggregate precision failed.");
        Check((await query.ExecuteAsync(q => q.Min(row => row.Amount), cancellationToken)) == 0.1m && (await query.ExecuteAsync(q => q.Max(row => row.Amount), cancellationToken)) == 0.3m, "Min/Max failed.");
        Check((await query.ExecuteAsync(q => q.Sum(row => row.Optional), cancellationToken)) == 6 && (await query.ExecuteAsync(q => q.Average(row => row.Optional), cancellationToken)) == 3d, "Nullable aggregates failed.");
        Check((await query.ExecuteAsync(q => q.Sum(row => row.Fraction), cancellationToken)) == 7.5, "Floating point sum failed.");
        Check((await query.ExecuteAsync(q => q.OrderByDescending(row => row.Number).Skip(1).Take(1).Sum(row => row.Amount), cancellationToken)) == 0.2m, "Aggregate ignored paging.");
        Check((await query.ExecuteAsync(q => q.Select(row => row.Amount).Sum(), cancellationToken)) == 0.6m && (await query.ExecuteAsync(q => q.Select(row => row.Number).Average(), cancellationToken)) == 2d, "Projected aggregate failed.");
        IQueryable<NumericRow> empty = query.Where(row => row.Number > 99);
        Check((await empty.ExecuteAsync(q => q.Sum(row => row.Number), cancellationToken)) == 0 && (await empty.ExecuteAsync(q => q.Sum(row => row.Optional), cancellationToken)) == 0, "Empty Sum failed.");
        Check((await empty.ExecuteAsync(q => q.Min(row => row.Optional), cancellationToken)) is null && (await empty.ExecuteAsync(q => q.Average(row => row.Optional), cancellationToken)) is null, "Empty nullable result failed.");
        try { _ = (await empty.ExecuteAsync(q => q.Average(row => row.Number), cancellationToken)); throw new Exception("Empty Average accepted."); } catch (InvalidOperationException) { }
        try { _ = (await empty.ExecuteAsync(q => q.Max(row => row.Amount), cancellationToken)); throw new Exception("Empty Max accepted."); } catch (InvalidOperationException) { }
        Check((await query.ExecuteAsync(q => q.Where(row => row.Number == 2).Sum(row => row.Optional), cancellationToken)) == 0, "All-null Sum failed.");
        Check((await query.ExecuteAsync(q => q.Where(row => row.Number == 2).Max(row => row.Optional), cancellationToken)) is null, "All-null Max failed.");
        var projected = (await query.Select(row => new { row.Number, row.Optional }).ToArrayAsync(cancellationToken));
        Check(projected.Length == 3 && projected[1].Optional is null, "JSON name mapping or nullable projection failed.");
        await container.AddItem("overflow", "{\"integer_value\":2147483647,\"amount\":0}", cancellationToken: cancellationToken);
        try { _ = (await query.ExecuteAsync(q => q.Sum(row => row.Number), cancellationToken)); throw new Exception("Integer overflow was ignored."); } catch (OverflowException) { }
    }

    private static async ValueTask Reject(Func<ValueTask> action)
    {
        try { await action(); throw new Exception("Unsupported expression was accepted."); }
        catch (NotSupportedException) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
