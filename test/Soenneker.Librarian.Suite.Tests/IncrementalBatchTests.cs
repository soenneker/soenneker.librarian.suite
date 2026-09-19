using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel]
public class IncrementalBatchTests
{
    public sealed class Row
    {
        public static int Created;
        public Row() => Interlocked.Increment(ref Created);
        public int Score { get; set; }
    }

    [Test]
    public async Task Batch_updates_only_changed_automatic_index_entries_and_preserves_no_op_indexes()
    {
        await using var fixture = new BatchFixture("memory");
        var items = await fixture.Database.GetContainer("items");
        for (int i = 0; i < 100; i++) await items.AddItem(i.ToString(), "{\"score\":1}");
        await items.EnsureIndex("score");
        var query = items.BuildQueryable<Row>();
        Check(query.Count(row => row.Score == 1) == 100);
        Row.Created = 0;
        await fixture.Database.Execute(new([new("items", "0", "{\"score\":2}"), new("items", "1", null)]));
        Check(Row.Created == 1 && query.Count(row => row.Score == 1) == 98 && Row.Created == 1);
        Check(await items.CountRangeByIndex("score", 2, 2) == 1);
        await fixture.Database.Execute(new([new("items", "0", "{\"score\":2}"), new("items", "absent", null)]));
        Check(query.Count(row => row.Score == 2) == 1 && Row.Created == 1);
    }

    [Test]
    public async Task Failed_filesystem_persistence_preserves_both_explicit_and_automatic_indexes()
    {
        await using var fixture = new PersistenceFixture();
        var items = await fixture.Database.GetContainer("items");
        await items.AddItem("id", "{\"score\":1}");
        await items.EnsureIndex("score");
        var query = items.BuildQueryable<Row>();
        Check(query.Count(row => row.Score == 1) == 1);
        await fixture.Database.Save();
        fixture.Files.BeforeWrite = (_, _) => throw new System.IO.IOException("Injected failure");
        try
        {
            await fixture.Database.Execute(new([new("items", "id", "{\"score\":2}")]));
            throw new Exception("Expected persistence failure.");
        }
        catch (System.IO.IOException) { }
        finally { fixture.Files.BeforeWrite = null; }
        Check(query.Count(row => row.Score == 1) == 1 && await items.CountByIndex("score", 1) == 1);
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Incremental batch indexes are incorrect or were rebuilt.");
    }
}
