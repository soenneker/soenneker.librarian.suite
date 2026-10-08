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
    public async ValueTask Shared_filters_ordering_pages_and_async_terminals(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer container = await fixture.Database.GetContainer("conformance", cancellationToken: cancellationToken);
        for (var i = 0; i < 12; i++) await container.AddItem(i.ToString("D2"), $"{{\"score\":{i / 3},\"name\":\"row-{i:D2}\",\"active\":true}}", cancellationToken: cancellationToken);
        IQueryable<ConformanceRow> root = container.BuildQueryable<ConformanceRow>();
        IQueryable<ConformanceRow> page = root.Where(row => row.Score >= 1).OrderBy(row => row.Score).Skip(2).Take(4);
        string[] expected = ["row-05", "row-06", "row-07", "row-08"];
        Check(page.Select(row => row.Name).ToArray().SequenceEqual(expected), "Tie ordering or paging differs.");
        Check((await page.Select(row => row.Name).ToArrayAsync(cancellationToken: cancellationToken)).SequenceEqual(expected), "Async page differs.");
        Check(await page.CountAsync(cancellationToken: cancellationToken) == 4 && await page.LongCountAsync(cancellationToken: cancellationToken) == 4, "Async page count differs.");
        Check(await root.AnyAsync(row => row.Score == 3, cancellationToken: cancellationToken) && !await root.AnyAsync(row => row.Score == 99, cancellationToken: cancellationToken), "Async existence differs.");
        Check((await root.Where(row => row.Name == "row-03").SingleAsync(cancellationToken: cancellationToken)).Name == "row-03", "Async Single differs.");
        Check(await root.Where(row => row.Score == 99).FirstOrDefaultAsync(cancellationToken: cancellationToken) is null, "Async default differs.");
        Check(await root.AllAsync(row => row.Active, cancellationToken: cancellationToken), "All differs.");
        string[] names = ["row-01", "row-06", "row-11"];
        var members = await root.Where(row => names.Contains(row.Name!)).OrderBy(row => row.Score).Take(3).Select(row => new { row.Name, row.Score }).ToListAsync(cancellationToken: cancellationToken);
        Check(members.Select(row => row.Name).SequenceEqual(names), "Membership or bounded projection differs.");
        Check(await root.CountAsync(row => row.Name!.StartsWith("row-0", StringComparison.Ordinal), cancellationToken: cancellationToken) == 10, "Ordinal prefix differs.");
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async ValueTask Explicit_indexes_share_null_precision_and_ordinal_rules(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer container = await fixture.Database.GetContainer("scalars", cancellationToken: cancellationToken);
        await container.AddItem("a", "{\"amount\":-79228162514264337593543950335,\"name\":null}", cancellationToken: cancellationToken);
        await container.AddItem("b", "{\"amount\":0.0000000000000000000000000001,\"name\":\"😀\"}", cancellationToken: cancellationToken);
        await container.AddItem("c", "{\"amount\":79228162514264337593543950335,\"name\":\"\\uE000\"}", cancellationToken: cancellationToken);
        await container.AddItem("d", "{}", cancellationToken: cancellationToken);
        await container.EnsureIndex("name", cancellationToken: cancellationToken);
        await container.EnsureIndex("amount", cancellationToken: cancellationToken);
        Check(await container.CountByIndex("name", null, cancellationToken: cancellationToken) == 1, "Explicit null includes missing data.");
        LibrarianQueryResult<ConformanceRow> strings = await container.FindRangeByIndex<ConformanceRow>("name", cancellationToken: cancellationToken);
        Check(strings.Items.Select(row => row.Name).SequenceEqual(new string?[] { null, "😀", "\uE000" }), "Explicit ordinal ordering differs.");
        LibrarianQueryResult<ConformanceRow> numbers = await container.FindRangeByIndex<ConformanceRow>("amount", decimal.MinValue, decimal.MaxValue, cancellationToken: cancellationToken);
        Check(numbers.Items.Select(row => row.Amount).SequenceEqual(new[] { decimal.MinValue, 0.0000000000000000000000000001m, decimal.MaxValue }), "Decimal precision differs.");
        int nullMatches = await container.BuildQueryable<ConformanceRow>().CountAsync(row => row.Name == null, cancellationToken: cancellationToken);
        Check(nullMatches == (provider is "memory" or "filesystem" ? 2 : 1), "Documented missing-property behavior changed.");
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async ValueTask Cancellation_and_lifetime_are_enforced_by_async_queries(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer container = await fixture.Database.GetContainer("lifetime", cancellationToken: cancellationToken);
        await container.AddItem("one", "{\"score\":1}", cancellationToken: cancellationToken);
        IQueryable<ConformanceRow> query = container.BuildQueryable<ConformanceRow>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await query.ToListAsync(cancellation.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        try { await query.CountAsync(cancellation.Token); throw new Exception("Count cancellation ignored."); } catch (OperationCanceledException) { }
        Check(await query.CountAsync(cancellationToken: cancellationToken) == 1, "Cancellation affected documents.");
        await fixture.Database.UnloadContainer("lifetime", cancellationToken: cancellationToken);
        try { await query.ToListAsync(cancellationToken: cancellationToken); throw new Exception("Disposed query accepted."); } catch (ObjectDisposedException) { }
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    public async ValueTask Core_projected_count_and_async_pages_avoid_extra_deserialization(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer container = await fixture.Database.GetContainer("cost", cancellationToken: cancellationToken);
        for (var i = 0; i < 100; i++) await container.AddItem(i.ToString("D3"), $"{{\"score\":{i},\"name\":\"row-{i}\"}}", cancellationToken: cancellationToken);
        IQueryable<ConformanceRow> source = container.BuildQueryable<ConformanceRow>().Where(row => row.Score >= 10);
        Check(source.Count() == 90, "Warm index count failed.");
        ConformanceRow.Created.Value = 0;
        IQueryable<string?> page = source.Select(row => row.Name).Skip(30).Take(4);
        Check(page.Count() == 4 && page.Any() && await page.CountAsync(cancellationToken: cancellationToken) == 4, "Projected aggregate failed.");
        Check(ConformanceRow.Created.Value == 0, "Projected count deserialized documents.");
        Check((await page.ToListAsync(cancellationToken: cancellationToken)).Count == 4 && ConformanceRow.Created.Value == 4, "Async scalar page deserialized skipped documents.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
