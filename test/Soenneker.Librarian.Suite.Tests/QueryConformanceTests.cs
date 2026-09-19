using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Suite.Tests;

public class QueryConformanceTests
{
    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async Task Shared_filters_ordering_pages_and_async_terminals(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer container = await fixture.Database.GetContainer("conformance");
        for (var i = 0; i < 12; i++) await container.AddItem(i.ToString("D2"), $"{{\"score\":{i / 3},\"name\":\"row-{i:D2}\",\"active\":true}}");
        IQueryable<ConformanceRow> root = container.BuildQueryable<ConformanceRow>();
        IQueryable<ConformanceRow> page = root.Where(row => row.Score >= 1).OrderBy(row => row.Score).Skip(2).Take(4);
        string[] expected = ["row-05", "row-06", "row-07", "row-08"];
        Check(page.Select(row => row.Name).ToArray().SequenceEqual(expected), "Tie ordering or paging differs.");
        Check((await page.Select(row => row.Name).ToArrayAsync()).SequenceEqual(expected), "Async page differs.");
        Check(await page.CountAsync() == 4 && await page.LongCountAsync() == 4, "Async page count differs.");
        Check(await root.AnyAsync(row => row.Score == 3) && !await root.AnyAsync(row => row.Score == 99), "Async existence differs.");
        Check((await root.Where(row => row.Name == "row-03").SingleAsync()).Name == "row-03", "Async Single differs.");
        Check(await root.Where(row => row.Score == 99).FirstOrDefaultAsync() is null, "Async default differs.");
        Check(await root.AllAsync(row => row.Active), "All differs.");
        string[] names = ["row-01", "row-06", "row-11"];
        var members = await root.Where(row => names.Contains(row.Name!)).OrderBy(row => row.Score).Take(3).Select(row => new { row.Name, row.Score }).ToListAsync();
        Check(members.Select(row => row.Name).SequenceEqual(names), "Membership or bounded projection differs.");
        Check(await root.CountAsync(row => row.Name!.StartsWith("row-0", StringComparison.Ordinal)) == 10, "Ordinal prefix differs.");
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async Task Explicit_indexes_share_null_precision_and_ordinal_rules(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer container = await fixture.Database.GetContainer("scalars");
        await container.AddItem("a", "{\"amount\":-79228162514264337593543950335,\"name\":null}");
        await container.AddItem("b", "{\"amount\":0.0000000000000000000000000001,\"name\":\"😀\"}");
        await container.AddItem("c", "{\"amount\":79228162514264337593543950335,\"name\":\"\\uE000\"}");
        await container.AddItem("d", "{}");
        await container.EnsureIndex("name");
        await container.EnsureIndex("amount");
        Check(await container.CountByIndex("name", null) == 1, "Explicit null includes missing data.");
        var strings = await container.FindRangeByIndex<ConformanceRow>("name");
        Check(strings.Items.Select(row => row.Name).SequenceEqual(new string?[] { null, "😀", "\uE000" }), "Explicit ordinal ordering differs.");
        var numbers = await container.FindRangeByIndex<ConformanceRow>("amount", decimal.MinValue, decimal.MaxValue);
        Check(numbers.Items.Select(row => row.Amount).SequenceEqual(new[] { decimal.MinValue, 0.0000000000000000000000000001m, decimal.MaxValue }), "Decimal precision differs.");
        int nullMatches = await container.BuildQueryable<ConformanceRow>().CountAsync(row => row.Name == null);
        Check(nullMatches == (provider is "memory" or "filesystem" ? 2 : 1), "Documented missing-property behavior changed.");
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async Task Cancellation_and_lifetime_are_enforced_by_async_queries(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer container = await fixture.Database.GetContainer("lifetime");
        await container.AddItem("one", "{\"score\":1}");
        IQueryable<ConformanceRow> query = container.BuildQueryable<ConformanceRow>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await query.ToListAsync(cancellation.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        try { await query.CountAsync(cancellation.Token); throw new Exception("Count cancellation ignored."); } catch (OperationCanceledException) { }
        Check(await query.CountAsync() == 1, "Cancellation affected documents.");
        await fixture.Database.UnloadContainer("lifetime");
        try { await query.ToListAsync(); throw new Exception("Disposed query accepted."); } catch (ObjectDisposedException) { }
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    public async Task Core_projected_count_and_async_pages_avoid_extra_deserialization(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer container = await fixture.Database.GetContainer("cost");
        for (var i = 0; i < 100; i++) await container.AddItem(i.ToString("D3"), $"{{\"score\":{i},\"name\":\"row-{i}\"}}");
        IQueryable<ConformanceRow> source = container.BuildQueryable<ConformanceRow>().Where(row => row.Score >= 10);
        Check(source.Count() == 90, "Warm index count failed.");
        ConformanceRow.Created.Value = 0;
        IQueryable<string?> page = source.Select(row => row.Name).Skip(30).Take(4);
        Check(page.Count() == 4 && page.Any() && await page.CountAsync() == 4, "Projected aggregate failed.");
        Check(ConformanceRow.Created.Value == 0, "Projected count deserialized documents.");
        Check((await page.ToListAsync()).Count == 4 && ConformanceRow.Created.Value == 4, "Async scalar page deserialized skipped documents.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    public sealed class ConformanceRow
    {
        public static readonly PostgresRow.ReadCounter Created = new();
        public ConformanceRow() => Created.Value++;
        public int Score { get; set; }
        public decimal Amount { get; set; }
        public string? Name { get; set; }
        public bool Active { get; set; }
    }
}
