using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using System.Text.Json;

namespace Soenneker.Librarian.Suite.Tests;

public class PostgresExpandedQueryTests
{
    [Test]
    public async Task Multi_key_ordering_and_projections_fetch_only_selected_fields()
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("projection");
        await container.AddItem("a", "{\"name\":\"B\",\"amount\":1,\"active\":true}");
        await container.AddItem("b", "{\"name\":\"A\",\"amount\":1,\"active\":false}");
        await container.AddItem("c", "{\"name\":\"C\",\"amount\":2,\"active\":true}");
        await container.AddItem("d", "{\"name\":\"D\",\"amount\":2,\"active\":false}");
        IQueryable<PostgresRow> query = container.BuildQueryable<PostgresRow>();
        PostgresRow.Reads.Value = 0;
        var page = query.OrderBy(row => row.Amount).ThenByDescending(row => row.Name)
            .Skip(1).Take(2).Select(row => new { row.Name, row.Active }).ToArray();
        Check(page.Select(row => row.Name).SequenceEqual(new[] { "A", "D" }), "Multiple ordering keys or page boundaries changed.");
        Check(PostgresRow.Reads.Value == 0, "Projection deserialized the source document.");
        var scalar = query.OrderByDescending(row => row.Amount).ThenBy(row => row.Name).Select(row => row.Name).Skip(1).Take(2).ToArray();
        Check(scalar.SequenceEqual(new[] { "D", "A" }), "Paging after projection changed ordering.");
        var dto = query.Where(row => row.Name == "B").Select(row => new ProjectionDto { Label = row.Name, Value = row.Amount }).Single();
        Check(dto.Label == "B" && dto.Value == 1, "Member initializer projection failed.");
        Check(query.Where(row => row.Name == "C").Select(row => new ProjectionRecord(row.Name!, row.Amount)).First().Amount == 2,
            "Constructor projection failed.");
        Check(query.Select(row => row.Name).Count() == 4, "Projection aggregate failed.");
        Check(query.Where(row => row.Amount == 9).Select(row => row.Amount).FirstOrDefault() == 0, "Scalar default was incorrect.");
        Check(query.Where(row => row.Amount == 9).Select(row => new { row.Name }).FirstOrDefault() is null, "Reference default was incorrect.");
        Check(PostgresRow.Reads.Value == 0, "Scalar projection fetched full objects.");
        Check(query.Select(row => row.Amount + 1).First() == 2, "Computed projection failed.");
        Check(query.Select(row => row.Name).Where(name => name == "A").Single() == "A", "Projected filter failed.");
        Reject(() => query.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Test]
    public async Task String_search_preserves_literals_and_utf16_boundaries()
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("strings");
        string[] names = ["A%_\\B", "AB", "prefix-tail", "prefix-😀-tail", "' OR 1=1 --", "", "Prefix-tail"];
        for (var i = 0; i < names.Length; i++) await container.AddItem(i.ToString(), JsonSerializer.Serialize(new { name = names[i] }));
        await container.AddItem("null", "{\"name\":null}");
        await container.AddItem("missing", "{}");
        IQueryable<PostgresRow> query = container.BuildQueryable<PostgresRow>();
        foreach (string term in new[] { "prefix", "tail", "%_\\", "😀", "' OR 1=1 --", "", "\u1004" })
        {
            Check(query.Count(row => row.Name!.StartsWith(term)) == names.Count(name => name.StartsWith(term, StringComparison.Ordinal)), "Prefix mismatch.");
            Check(query.Count(row => row.Name!.EndsWith(term, StringComparison.Ordinal)) == names.Count(name => name.EndsWith(term, StringComparison.Ordinal)), "Suffix mismatch.");
            Check(query.Count(row => row.Name!.Contains(term)) == names.Count(name => name.Contains(term, StringComparison.Ordinal)), "Substring mismatch.");
        }
        Check(query.Count(row => row.Name!.StartsWith("prefix") && row.Name.EndsWith("tail")) == 2, "Composed string predicates failed.");
        Reject(() => query.Count(row => row.Name!.Contains("PREFIX", StringComparison.OrdinalIgnoreCase)));
        Reject(() => query.Count(row => row.Name!.StartsWith("prefix", StringComparison.CurrentCulture)));
    }

    [Test]
    public async Task Collection_membership_all_and_boolean_constants_execute_on_server()
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("membership");
        await container.AddItem("a", "{\"name\":\"A\",\"amount\":1}");
        await container.AddItem("b", "{\"name\":\"B\",\"amount\":2}");
        await container.AddItem("c", "{\"name\":null,\"amount\":3}");
        await container.AddItem("d", "{\"amount\":4}");
        IQueryable<PostgresRow> query = container.BuildQueryable<PostgresRow>();
        string?[] names = ["A", null, "A"];
        var numbers = new List<decimal> { 2, 3 };
        var set = new HashSet<string> { "A", "B" };
        Check(query.Count(row => names.Contains(row.Name)) == 2, "IN did not preserve explicit null or duplicate semantics.");
        Check(query.Count(row => numbers.Contains(row.Amount)) == 2, "List membership failed.");
        Check(query.Count(row => set.Contains(row.Name!)) == 2, "Set membership failed.");
        Check(query.Count(row => new[] { "A", "B" }.Contains(row.Name)) == 2, "Inline array membership failed.");
        decimal[] empty = [];
        Check(!query.Any(row => empty.Contains(row.Amount)), "Empty membership matched rows.");
        Check(query.All(row => row.Amount >= 1) && !query.All(row => row.Amount > 1), "All failed.");
        Check(query.Where(row => row.Amount > 99).All(row => row.Amount < 0), "Empty All must be true.");
        Check(query.Count(row => true) == 4 && !query.Any(row => false), "Constant predicate failed.");
        PostgresRow.Reads.Value = 0;
        Check(query.All(row => !empty.Contains(row.Amount)), "Negated membership failed.");
        Check(PostgresRow.Reads.Value == 0, "All deserialized documents.");
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a" };
        Reject(() => query.Count(row => insensitive.Contains(row.Name!)));
    }

    [Test]
    public async Task Numeric_aggregates_preserve_types_paging_and_empty_results()
    {
        await using var fixture = new PostgresPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("aggregates");
        NumericRow[] rows = [new() { Number = 1, Amount = 0.1m, Optional = 2, Fraction = 1.25 },
            new() { Number = 2, Amount = 0.2m, Optional = null, Fraction = 2.5 },
            new() { Number = 3, Amount = 0.3m, Optional = 4, Fraction = 3.75 }];
        for (var i = 0; i < rows.Length; i++) await container.AddItem(i.ToString(), JsonSerializer.Serialize(rows[i], TestJsonContext.Default.PostgresExpandedQueryTestsNumericRow));
        IQueryable<NumericRow> query = container.BuildQueryable<NumericRow>();
        Check(query.Sum(row => row.Number) == 6, "Integer Sum failed.");
        Check(query.Average(row => row.Number) == 2d, "Integer Average return type failed.");
        Check(query.Sum(row => row.Amount) == 0.6m && query.Average(row => row.Amount) == 0.2m, "Decimal aggregate precision failed.");
        Check(query.Min(row => row.Amount) == 0.1m && query.Max(row => row.Amount) == 0.3m, "Min/Max failed.");
        Check(query.Sum(row => row.Optional) == 6 && query.Average(row => row.Optional) == 3d, "Nullable aggregates failed.");
        Check(query.Sum(row => row.Fraction) == 7.5, "Floating point sum failed.");
        Check(query.OrderByDescending(row => row.Number).Skip(1).Take(1).Sum(row => row.Amount) == 0.2m, "Aggregate ignored paging.");
        Check(query.Select(row => row.Amount).Sum() == 0.6m && query.Select(row => row.Number).Average() == 2d, "Projected aggregate failed.");
        IQueryable<NumericRow> empty = query.Where(row => row.Number > 99);
        Check(empty.Sum(row => row.Number) == 0 && empty.Sum(row => row.Optional) == 0, "Empty Sum failed.");
        Check(empty.Min(row => row.Optional) is null && empty.Average(row => row.Optional) is null, "Empty nullable result failed.");
        try { empty.Average(row => row.Number); throw new Exception("Empty Average accepted."); } catch (InvalidOperationException) { }
        try { empty.Max(row => row.Amount); throw new Exception("Empty Max accepted."); } catch (InvalidOperationException) { }
        Check(query.Where(row => row.Number == 2).Sum(row => row.Optional) == 0, "All-null Sum failed.");
        Check(query.Where(row => row.Number == 2).Max(row => row.Optional) is null, "All-null Max failed.");
        var projected = query.Select(row => new { row.Number, row.Optional }).ToArray();
        Check(projected.Length == 3 && projected[1].Optional is null, "JSON name mapping or nullable projection failed.");
        await container.AddItem("overflow", "{\"integer_value\":2147483647,\"amount\":0}");
        try { query.Sum(row => row.Number); throw new Exception("Integer overflow was ignored."); } catch (OverflowException) { }
    }

    private static void Reject(Action action)
    {
        try { action(); throw new Exception("Unsupported expression was accepted."); }
        catch (NotSupportedException) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public sealed class ProjectionDto
    {
        public string? Label { get; set; }
        public decimal Value { get; set; }
    }

    public sealed record ProjectionRecord(string Name, decimal Amount);

    public sealed class NumericRow
    {
        [JsonPropertyName("integer_value")]
        public int Number { get; set; }
        public decimal Amount { get; set; }
        public int? Optional { get; set; }
        public double Fraction { get; set; }
    }
}
