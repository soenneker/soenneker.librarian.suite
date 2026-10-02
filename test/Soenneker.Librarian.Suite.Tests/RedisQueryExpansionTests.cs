using System;
using System.Linq;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Transactions;
using StackExchange.Redis;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel("Redis")]
public class RedisQueryExpansionTests
{
    [Test]
    public async ValueTask Compound_pages_cross_windows_preserve_ties_and_clean_temporary_sets()
    {
        await using var fixture = new RedisPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items");
        var rows = Enumerable.Range(0, 350).Select(i => new RedisRow { Name = $"row-{i:D4}", Amount = i % 11 }).ToArray();
        await fixture.Database.Execute(new LibrarianBatch(rows.Select(row => new LibrarianWrite("items", row.Name,
            $"{{\"name\":\"{row.Name}\",\"amount\":{row.Amount}}}"))));
        await container.AddItem("missing", "{\"name\":\"missing\"}");
        string[] names = rows.Take(300).Select(row => row.Name).ToArray();
        IQueryable<RedisRow> query = container.BuildQueryable<RedisRow>();
        foreach (int take in new[] { 1, 128, 256, 300 })
        {
            string[] actual = await query.Where(row => names.Contains(row.Name) && row.Amount >= 2)
                .OrderByDescending(row => row.Amount).Skip(17).Take(take).Select(row => row.Name).ToArrayAsync();
            string[] expected = rows.Where(row => names.Contains(row.Name) && row.Amount >= 2)
                .OrderByDescending(row => row.Amount).ThenByDescending(row => row.Name, StringComparer.Ordinal)
                .Skip(17).Take(take).Select(row => row.Name).ToArray();
            Check(actual.SequenceEqual(expected), "Compound page changed ordering or membership across a window.");
        }
        Check(await query.CountAsync(row => !names.Contains(row.Name)) == 51, "Negation omitted missing properties.");
        Check((await query.Where(row => row.Name == "row-0349" || row.Name == "missing")
            .OrderBy(row => row.Amount).Take(2).ToArrayAsync()).Single().Name == "row-0349",
            "Sparse ordered page included a missing ordering property.");
        IDatabase store = await fixture.GetStore();
        IServer server = store.Multiplexer.GetServer((await store.IdentifyEndpointAsync(fixture.RedisPrefix + "items:ids"))!);
        await foreach (RedisKey key in server.KeysAsync(pattern: fixture.RedisPrefix + "items:query:*"))
            throw new Exception("Query left a temporary key: " + key.ToString());
    }

    [Test]
    public async ValueTask Old_indexes_gain_stable_sort_keys_and_writes_keep_them_current()
    {
        await using var fixture = new RedisPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items");
        foreach (string id in new[] { "c", "b", "a" }) await container.AddItem(id, $"{{\"name\":\"{id}\",\"amount\":1}}");
        await container.EnsureIndex("amount");
        IDatabase store = await fixture.GetStore();
        string prefix = fixture.RedisPrefix + "items:";
        await store.KeyDeleteAsync(prefix + "sort-schema");
        foreach (string id in new[] { "A", "B", "C" }) await store.HashDeleteAsync(prefix + "document:" + id, "sort:amount");
        IQueryable<RedisRow> query = container.BuildQueryable<RedisRow>();
        Check((await query.OrderBy(row => row.Amount).Take(3).Select(row => row.Name).ToArrayAsync()).SequenceEqual(new[] { "a", "b", "c" }), "Legacy sort upgrade failed.");
        Check(await store.SetContainsAsync(prefix + "sort-schema", "amount"), "Upgrade marker missing.");
        await container.UpdateItemStrict("a", "{\"name\":\"a\",\"amount\":2}");
        await fixture.Database.Execute(new LibrarianBatch([new LibrarianWrite("items", "b", "{\"name\":\"b\",\"amount\":3}")]));
        Check((await query.OrderByDescending(row => row.Amount).Take(3).Select(row => row.Name).ToArrayAsync()).SequenceEqual(new[] { "b", "a", "c" }), "Mutation sort fields became stale.");
        await container.DeleteAllItems();
        await container.AddItem("x", "{\"name\":\"x\",\"amount\":4}");
        Check((await query.OrderBy(row => row.Amount).FirstAsync()).Name == "x", "Sort field lost after clearing documents.");
    }

    [Test]
    public async ValueTask Membership_prefix_and_bounded_projection_do_not_enable_unbounded_fallback()
    {
        await using var fixture = new RedisPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items");
        await container.AddItem("a", "{\"name\":\"A%_\\\\tail\",\"amount\":1}");
        await container.AddItem("b", "{\"name\":\"Ab\",\"amount\":2}");
        IQueryable<RedisRow> query = container.BuildQueryable<RedisRow>();
        string[] names = ["Ab", "Ab"];
        Check(await query.CountAsync(row => names.Contains(row.Name)) == 1, "Duplicate membership changed count.");
        Check(await query.CountAsync(row => new[] { "Ab" }.Contains(row.Name)) == 1, "Inline membership failed.");
        Check(await query.CountAsync(row => row.Name.StartsWith("A%_\\", StringComparison.Ordinal)) == 1, "Prefix interpreted literal characters.");
        Check(!await query.AllAsync(row => row.Amount == 1), "All did not find a counterexample.");
        Check((await query.Take(1).Select(row => new { row.Name, row.Amount }).ToListAsync()).Count == 1, "Bounded projection failed.");
        try { await query.Select(row => row.Name).ToListAsync(); throw new Exception("Unbounded projection accepted."); } catch (NotSupportedException) { }
        try { await query.CountAsync(row => row.Name.EndsWith("tail")); throw new Exception("Suffix fallback accepted."); } catch (NotSupportedException) { }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
