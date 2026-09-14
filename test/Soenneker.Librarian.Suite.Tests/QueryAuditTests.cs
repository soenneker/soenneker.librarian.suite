using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Memory;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel]
public class QueryAuditTests
{

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    [Test]
    public async Task Secondary_ordering_preserves_primary_order_and_paging()
    {
        await using var db = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await db.GetContainer("audit");
        for (var i = 0; i < 30; i++) await container.AddItem(i.ToString(), $"{{\"score\":{i % 3},\"name\":\"{30-i:D2}\"}}");
        IQueryable<AuditRow> root = container.BuildQueryable<AuditRow>();
        AuditRow[] reference = root.ToArray();
        string[] actual = root.OrderBy(row => row.Score).ThenBy(row => row.Name).Skip(3).Take(12).Select(row => row.Name).ToArray();
        Check(actual.SequenceEqual(reference.OrderBy(row => row.Score).ThenBy(row => row.Name).Skip(3).Take(12).Select(row => row.Name)), "ThenBy changed ordering");
        Check(root.Where(row => row.Score >= 1).OrderByDescending(row => row.Score).ThenByDescending(row => row.Name).Take(4).Select(row => row.Name)
            .SequenceEqual(reference.Where(row => row.Score >= 1).OrderByDescending(row => row.Score).ThenByDescending(row => row.Name).Take(4).Select(row => row.Name)), "Descending ThenBy changed ordering");
    }

    [Test]
    public async Task Separate_filters_stay_indexed_and_projection_only_reads_the_page()
    {
        await using var db = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await db.GetContainer("audit");
        for (var i = 0; i < 300; i++) await container.AddItem(i.ToString(), $"{{\"score\":{i}}}");
        IQueryable<AuditRow> root = container.BuildQueryable<AuditRow>();
        root.Count(row => row.Score == 0);
        AuditRow.Created = 0;
        IQueryable<AuditRow> query = root.Where(row => row.Score >= 100).Where(row => row.Score < 200).OrderBy(row => row.Score).Skip(5).Take(10);
        Check(query.Select(row => row.Score).SequenceEqual(Enumerable.Range(105,10)), "Split filters failed");
        Check(AuditRow.Created == 10, "Split filters materialized extra documents");
        AuditRow.Created = 0;
        Check(root.Where(row => row.Score >= 100).Select(row => row.Score + 1).Take(10).ToArray().SequenceEqual(Enumerable.Range(101, 10)), "Trailing Take changed projection");
        Check(AuditRow.Created == 10, "Select before Take loaded all matches");
    }

    [Test]
    public async Task Fallback_and_unindexed_paging_only_deserialize_consumed_documents()
    {
        await using var db = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await db.GetContainer("audit");
        for (var i = 0; i < 300; i++) await container.AddItem(i.ToString(), $"{{\"score\":{i}}}");
        IQueryable<AuditRow> root = container.BuildQueryable<AuditRow>();
        AuditRow.Created = 0;
        Check(root.Take(10).ToArray().Length == 10 && AuditRow.Created == 10, "Unindexed Take deserialized everything");
        AuditRow.Created = 0;
        Check(root.Where(row => row.Score % 2 == 0).Take(10).ToArray().Length == 10 && AuditRow.Created < 300, "Fallback eagerly deserialized everything");
    }

    [Test]
    public async Task Reusable_projections_observe_closures_writes_and_preserve_exceptions()
    {
        await using var db = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await db.GetContainer("audit");
        await container.AddItem("one", "{\"score\":1}");
        var offset = 3;
        IQueryable<int> query = container.BuildQueryable<AuditRow>().Where(row => row.Score >= 0).Select(row => row.Score + offset);
        Check(query.Single() == 4, "Projection failed");
        offset = 7;
        await container.UpdateItemStrict("one", "{\"score\":2}");
        Check(query.Single() == 9, "Projection cached data or closure value");
        Check(container.BuildQueryable<AuditRow>().Select((row, index) => row.Score + index).Single() == 2, "Indexed Select failed");
        IQueryable<int> fault = container.BuildQueryable<AuditRow>().Select(row => Fail(row));
        var threw = false;
        try { fault.ToArray(); } catch (InvalidOperationException) { threw = true; }
        Check(threw, "Projection exception changed");
    }

    private static int Fail(AuditRow row) => throw new InvalidOperationException("Expected");

    [Test]
    public async Task Field_backed_custom_getters_are_not_treated_as_stable_index_keys()
    {
        await using var db = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await db.GetContainer("audit");
        await container.AddItem("one", "{\"score\":5}");
        IQueryable<FieldRow> root = container.BuildQueryable<FieldRow>();
        FieldRow.Adjustment = 0;
        Check(root.Count(row => row.Score == 5) == 1, "Custom getter failed");
        FieldRow.Adjustment = 1;
        Check(root.Count(row => row.Score == 6) == 1 && root.Count(row => row.Score == 5) == 0, "Custom getter was cached in an index");
    }

    [Test]
    public async Task Randomized_composed_queries_match_linq_to_objects()
    {
        await using var db = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await db.GetContainer("audit");
        for (var i = 0; i < 80; i++) await container.AddItem(i.ToString(), $"{{\"score\":{i},\"name\":\"group-{i % 4}\"}}");
        IQueryable<AuditRow> root = container.BuildQueryable<AuditRow>();
        IQueryable<AuditRow> reference = root.ToArray().AsQueryable();
        var random = new Random(761);
        for (var i = 0; i < 160; i++)
        {
            int low = random.Next(-5, 80), high = random.Next(0, 90), skip = random.Next(-2, 30), take = random.Next(-2, 30);
            foreach (Func<IQueryable<AuditRow>, IQueryable<AuditRow>> build in new Func<IQueryable<AuditRow>, IQueryable<AuditRow>>[]
            {
                q => q.Where(row => row.Score >= low).Where(row => row.Score <= high).OrderBy(row => row.Score).Skip(skip).Take(take),
                q => q.OrderByDescending(row => row.Score).Where(row => row.Score > low && row.Score < high).Skip(skip).Take(take),
                q => q.Where(row => row.Score >= low).Where(row => row.Score < high && row.Name == "group-2").OrderBy(row => row.Score).Take(take),
                q => q.OrderBy(row => row.Score).Take(take).Where(row => row.Score >= low).Skip(skip),
                q => q.OrderBy(row => row.Name).ThenByDescending(row => row.Score).Skip(skip).Take(take),
                q => q.Where(row => low <= row.Score && high > row.Score).OrderBy(row => row.Score).Skip(skip).Take(take)
            })
            {
                int[] expected = build(reference).Select(row => row.Score).ToArray();
                IQueryable<AuditRow> actual = build(root);
                Check(actual.Select(row => row.Score).SequenceEqual(expected), $"Result mismatch at {i}");
                Check(actual.Count() == expected.Length && actual.Any() == (expected.Length > 0), $"Terminal mismatch at {i}");
            }
        }
    }

    [Test]
    public async Task Scan_snapshots_are_detached_invalidated_on_mutation_and_skip_invalid_json()
    {
        await using var db = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await db.GetContainer("audit");
        await container.AddItem("one", "{\"score\":1}");
        await container.AddItem("bad", "invalid");
        await container.AddItem("null", "null");
        IQueryable<AuditRow> root = container.BuildQueryable<AuditRow>();
        using IEnumerator<AuditRow> snapshot = root.GetEnumerator();
        await container.UpdateItemStrict("one", "{\"score\":2}");
        Check(snapshot.MoveNext() && snapshot.Current.Score == 1 && !snapshot.MoveNext(), "Scan snapshot changed during enumeration");
        Check(root.Single().Score == 2, "Update did not invalidate scan");
        await container.AddItem("two", "{\"score\":3}");
        Check(root.Count() == 2, "Add did not invalidate scan");
        await container.DeleteItem("one");
        Check(root.Single().Score == 3, "Delete did not invalidate scan");
        await container.DeleteAllItems();
        Check(!root.Any(), "Clear did not invalidate scan");
    }

    [Test]
    public async Task Query_roots_are_cached_deferred_and_support_the_standard_provider_contract()
    {
        await using var db = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await db.GetContainer("audit");
        await container.AddItem("one", "{\"score\":1}");
        IQueryable<AuditRow> root = container.BuildQueryable<AuditRow>();
        AuditRow.Created = 0;
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
            if (!ReferenceEquals(root, container.BuildQueryable<AuditRow>())) throw new Exception("Root not cached");
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Check(bytes == 0 && AuditRow.Created == 0, "Warm root construction allocated or materialized documents");
        Expression expression = root.Where(row => row.Score == 1).Expression;
        Check(root.Provider.CreateQuery(expression).Cast<AuditRow>().Single().Score == 1, "Non-generic CreateQuery failed");
        MethodCallExpression count = Expression.Call(typeof(Queryable), nameof(Queryable.Count), new[] { typeof(AuditRow) }, expression);
        Check((int)root.Provider.Execute(count)! == 1, "Non-generic Execute failed");
        var threw = false;
        try { root.Provider.CreateQuery(Expression.Constant(1)); } catch (ArgumentException) { threw = true; }
        Check(threw, "Invalid query expression accepted");
    }
}
