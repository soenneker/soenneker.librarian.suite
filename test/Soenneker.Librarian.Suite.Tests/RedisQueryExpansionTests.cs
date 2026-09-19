using System;
using System.Linq;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using StackExchange.Redis;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel("Redis")]
public class RedisQueryExpansionTests
{
    [Test]
    public async Task Old_indexes_gain_stable_sort_keys_and_writes_keep_them_current()
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
        await fixture.Database.Execute(new([new("items", "b", "{\"name\":\"b\",\"amount\":3}")]));
        Check((await query.OrderByDescending(row => row.Amount).Take(3).Select(row => row.Name).ToArrayAsync()).SequenceEqual(new[] { "b", "a", "c" }), "Mutation sort fields became stale.");
        await container.DeleteAllItems();
        await container.AddItem("x", "{\"name\":\"x\",\"amount\":4}");
        Check((await query.OrderBy(row => row.Amount).FirstAsync()).Name == "x", "Sort field lost after clearing documents.");
    }

    [Test]
    public async Task Membership_prefix_and_bounded_projection_do_not_enable_unbounded_fallback()
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
