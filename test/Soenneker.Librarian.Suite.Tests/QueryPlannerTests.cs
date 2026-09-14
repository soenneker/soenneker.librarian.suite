using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Memory;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel]
public class QueryPlannerTests
{

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static MemoryLibrarianDatabase Database() => new(NullLogger<MemoryLibrarianDatabase>.Instance);
    private static async Task<Soenneker.Librarian.Abstractions.ILibrarianContainer> Populate(MemoryLibrarianDatabase database)
    {
        ILibrarianContainer container = await database.GetContainer("planner");
        for (var i = 0; i < 2000; i++)
            await container.AddItem($"{i:D5}", $"{{\"score\":{i},\"status\":\"{(i % 100 == 0 ? "target" : "other")}\",\"name\":\"{(i % 3 == 0 ? "ok" : "no")}\",\"active\":{(i % 2 == 0 ? "true" : "false")}}}");
        return container;
    }

    [Test]
    public async Task Composite_indexes_build_in_one_pass_and_only_materialize_the_page()
    {
        await using MemoryLibrarianDatabase database = Database();
        ILibrarianContainer container = await Populate(database);
        IQueryable<PlannerRow> root = container.BuildQueryable<PlannerRow>();
        PlannerRow.Created = 0;
        Check(root.Count(row => row.Score >= 0 && row.Status == "target" && row.Active) == 20, "Cold count failed");
        Check(PlannerRow.Created == 2000, "Cold indexes deserialized the container more than once");
        PlannerRow.Created = 0;
        IQueryable<PlannerRow> query = root.Where(row => 100 <= row.Score && row.Status == "target" && row.Active).OrderByDescending(row => row.Score).Skip(2).Take(3);
        Check(query.ToArray().Select(row => row.Score).SequenceEqual(new[] { 1700, 1600, 1500 }), "Multi-index ordering/paging failed");
        Check(PlannerRow.Created == 3, "Multi-index query materialized more than its page");
        PlannerRow.Created = 0;
        Check(query.Count() == 3 && query.Any(), "Composite terminals failed");
        Check(root.Count(row => row.Status == "target" && !row.Active) == 0, "Boolean negation failed");
        Check(PlannerRow.Created == 0, "Count/Any materialized documents");
        await container.UpdateItemStrict("01700", "{\"score\":17,\"status\":\"other\",\"active\":false}");
        PlannerRow.Created = 0;
        Check(query.ToArray().Select(row => row.Score).SequenceEqual(new[] { 1600, 1500, 1400 }) && PlannerRow.Created == 3, "Index maintenance changed composite results");
    }

    [Test]
    public async Task Ordering_on_another_property_and_projected_paging_use_index_keys()
    {
        await using MemoryLibrarianDatabase database = Database();
        ILibrarianContainer container = await Populate(database);
        IQueryable<PlannerRow> root = container.BuildQueryable<PlannerRow>();
        // Warm both indexes, independently of the query under test.
        root.Count(row => row.Status == "target" && row.Score >= 0);
        PlannerRow.Created = 0;
        Check(root.Where(row => row.Status == "target").OrderBy(row => row.Score).Skip(2).Take(3).ToArray()
            .Select(row => row.Score).SequenceEqual(new[] { 200, 300, 400 }), "Other-property ordering failed");
        Check(PlannerRow.Created == 3, "Sorting deserialized candidate documents");
        PlannerRow.Created = 0;
        Check(root.Where(row => row.Score >= 0).Select(row => row.Score).Skip(1900).Take(3).ToArray()
            .SequenceEqual(new[] { 1900, 1901, 1902 }), "Projected rank paging failed");
        Check(PlannerRow.Created == 3, "Projected paging deserialized skipped rows");
        PlannerRow.Created = 0;
        Check(root.Where(row => row.Score >= 0).Select(row => row.Score + 1).Skip(10).Take(3).ToArray()
            .SequenceEqual(new[] { 11, 12, 13 }), "Computed projection changed");
        Check(PlannerRow.Created <= 13, "Computed projection eagerly loaded all candidates");
    }

    [Test]
    public async Task Residual_predicates_reuse_indexed_prefixes_and_stop_at_take()
    {
        await using MemoryLibrarianDatabase database = Database();
        ILibrarianContainer container = await Populate(database);
        IQueryable<PlannerRow> root = container.BuildQueryable<PlannerRow>();
        root.Count(row => row.Score >= 0 && row.Status == "target");
        PlannerRow.Created = 0;
        IQueryable<PlannerRow> combined = root.Where(row => row.Score >= 0 && row.Status == "target" && row.Name.StartsWith("ok")).Take(3);
        Check(combined.ToArray().Select(row => row.Score).SequenceEqual(new[] { 0, 300, 600 }), "Combined residual filtering failed");
        Check(PlannerRow.Created == 7, "Residual filtering read documents outside the required candidates");
        PlannerRow.Created = 0;
        Check(root.Where(row => row.Status == "target").Where(row => row.Name.StartsWith("ok")).Take(3).ToArray().Length == 3 && PlannerRow.Created == 7,
            "Separate residual Where abandoned the index");
        Check(combined.Concat(root).Count() == 2003, "Residual replacement leaked into another query branch");
        Check(root.Where(row => row.Score == 1 && row.Name.StartsWith("ok")).Count() == 0, "Residual predicate was skipped by Count");
    }

    [Test]
    public async Task Multi_property_plans_match_reference_queries_across_bounds_and_pages()
    {
        await using MemoryLibrarianDatabase database = Database();
        ILibrarianContainer container = await Populate(database);
        IQueryable<PlannerRow> root = container.BuildQueryable<PlannerRow>();
        IQueryable<PlannerRow> reference = root.ToArray().AsQueryable();
        var random = new Random(881);
        for (var i = 0; i < 120; i++)
        {
            int low = random.Next(-1, 2000), high = random.Next(0, 2100), skip = random.Next(-1, 25), take = random.Next(-1, 25);
            bool active = random.Next(2) == 0;
            foreach (Func<IQueryable<PlannerRow>, IQueryable<PlannerRow>> build in new Func<IQueryable<PlannerRow>, IQueryable<PlannerRow>>[]
            {
                q => q.Where(row => row.Score >= low && row.Status == "target" && row.Score < high).OrderByDescending(row => row.Score).Skip(skip).Take(take),
                q => q.Where(row => row.Status == "other").Where(row => row.Active == active).Where(row => row.Score >= low && row.Score < high).OrderBy(row => row.Score).Skip(skip).Take(take),
                q => q.OrderBy(row => row.Score).Where(row => row.Status == "target" && row.Score < high).Take(take).Where(row => row.Score >= low),
                q => q.Where(row => row.Status == "target" && row.Name.StartsWith("ok")).OrderBy(row => row.Score).Skip(skip).Take(take),
                q => q.Where(row => row.Status == "other" && row.Score >= low && row.Active).OrderBy(row => row.Score).ThenBy(row => row.Name).Take(take)
            })
            {
                int[] expected = build(reference).Select(row => row.Score).ToArray();
                IQueryable<PlannerRow> actual = build(root);
                Check(actual.Select(row => row.Score).ToArray().SequenceEqual(expected), $"Sequence mismatch at {i}");
                Check(actual.Count() == expected.Length && actual.Any() == (expected.Length > 0), $"Terminal mismatch at {i}");
            }
        }
    }

    [Test]
    public async Task Residual_short_circuiting_and_computed_projection_evaluation_are_preserved()
    {
        await using MemoryLibrarianDatabase database = Database();
        ILibrarianContainer container = await Populate(database);
        IQueryable<PlannerRow> root = container.BuildQueryable<PlannerRow>();
        Check(root.Where(row => row.Score == -1 && Fails(row)).Take(1).ToArray().Length == 0, "False indexed prefix evaluated residual");
        var threw = false;
        try { root.Where(row => Fails(row) && row.Score == -1).Take(1).ToArray(); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw, "Planner moved a filter before a throwing residual");
        Check(root.Where(row => row.Score == 1 || row.Status == "target").Count() == 21, "OR was incorrectly narrowed to one branch");
        threw = false;
        try { root.Where(row => row.Score >= 0).Select(row => Fails(row)).Skip(1).Take(1).ToArray(); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw, "Planner skipped evaluation of a computed projection");
    }

    private static bool Fails(PlannerRow row) => throw new InvalidOperationException("Expected");

    [Test]
    public async Task Composite_plans_remain_consistent_during_concurrent_creation_and_writes()
    {
        await using MemoryLibrarianDatabase database = Database();
        ILibrarianContainer container = await database.GetContainer("concurrent");
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            await container.AddItem(i.ToString(), $"{{\"score\":{i},\"status\":\"target\",\"active\":true}}");
            Check(container.BuildQueryable<PlannerRow>().Any(row => row.Score == i && row.Status == "target" && row.Active), "Concurrent insert missing");
            await container.UpdateItemStrict(i.ToString(), $"{{\"score\":{i + 1000},\"status\":\"target\",\"active\":false}}");
        })));
        Check(container.BuildQueryable<PlannerRow>().Count(row => row.Score >= 1000 && row.Status == "target" && !row.Active) == 100,
            "Concurrent index maintenance lost data");
    }
}
