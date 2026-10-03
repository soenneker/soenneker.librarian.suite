using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class MongoNativeLinqTests
{
    [Test]
    public async Task Native_grouping_arrays_aggregates_and_unbounded_projections()
    {
        await using MongoPersistenceFixture fixture = await MongoPersistenceFixture.Create();
        ILibrarianContainer items = await fixture.Database.GetContainer("items");
        await items.AddItem("a", """{"score_value":1,"name":"alpha","amount":1.25,"scores":[1,2],"details":{"region":"east"},"status":"Active","optional":3,"metrics":{"clicks":5},"stringNumber":"7"}""");
        await items.AddItem("b", """{"score_value":2,"name":"beta","amount":2.50,"scores":[2,3],"details":{"region":"east"},"status":"Pending"}""");
        await items.AddItem("c", """{"score_value":3,"name":"gamma","amount":3.75,"scores":[4],"details":{"region":"west"},"status":"Active"}""");
        await (await fixture.Database.GetContainer("other")).AddItem("hidden", """{"score_value":999,"name":"hidden"}""");
        IQueryable<MongoLinqRow> query = items.BuildQueryable<MongoLinqRow>();

        var groups = await query.GroupBy(row => row.Details.Region)
            .Select(group => new { Region = group.Key, Count = group.Count(), Total = group.Sum(row => row.Amount) })
            .OrderBy(group => group.Region).ToListAsync();
        Check(groups.Count == 2 && groups[0].Count == 2 && groups[0].Total == 3.75m, "Server grouping/decimal sum failed.");
        Check(await query.ExecuteAsync(rows => rows.Sum(row => row.Amount)) == 7.50m, "Async Sum failed.");
        Check(query.Average(row => row.Amount) == 2.50m && query.Max(row => row.Score) == 3, "Native aggregates failed.");
        Check(await query.Where(row => row.Scores.Any(score => score > 3)).CountAsync() == 1, "Nested Any failed.");
        Check((await query.SelectMany(row => row.Scores).Distinct().OrderBy(score => score).ToListAsync()).SequenceEqual([1, 2, 3, 4]), "SelectMany/Distinct failed.");
        Check(await query.CountAsync(row => row.Status == RedisStatus.Active && row.Optional.HasValue) == 1, "Enum/nullable query failed.");
        Check(await query.CountAsync(row => row.Metrics["clicks"] == 5) == 1, "Dictionary member query failed.");
        Check(await query.CountAsync(row => row.StringNumber == 7) == 1, "Member JSON number handling was lost.");
        var projected = await query.OrderBy(row => row.Details.Region).ThenByDescending(row => row.Score)
            .Select(row => new { Label = row.Name.ToUpper(), Score = row.Score + 10 })
            .Where(row => row.Score > 11).ToListAsync();
        Check(projected.Count == 2 && projected[0].Label == "BETA", "Computed unbounded projection or ThenBy failed.");
        Check(await query.Where(row => row.Name.Contains("amm")).SingleAsync() is { Score: 3, Amount: 3.75m, Scores.Count: 1 }, "Native document materialization failed.");
        Check(query.OrderBy(row => row.Score).Take(2).Where(row => row.Score > 1).Single().Name == "beta", "Filter after paging failed.");
        Check(query.ToString()!.Contains("$match", StringComparison.Ordinal), "Query did not retain a native pipeline.");
        Check(await MongoDB.Driver.Linq.MongoQueryable.CountAsync(query) == 3, "Driver async extensions failed.");
        using var cursor = await MongoDB.Driver.Linq.MongoQueryable.ToCursorAsync(query);
        Check(await cursor.MoveNextAsync() && cursor.Current.Count() == 3, "Driver cursor execution failed.");
        Check(((MongoDB.Driver.Linq.IMongoQueryProvider)query.Provider).LoggedStages.Length > 0, "No server pipeline was executed.");
    }

    [Test]
    public async Task Unsupported_native_join_does_not_bypass_container_filters()
    {
        await using MongoPersistenceFixture fixture = await MongoPersistenceFixture.Create();
        ILibrarianContainer left = await fixture.Database.GetContainer("left");
        ILibrarianContainer right = await fixture.Database.GetContainer("right");
        await left.AddItem("a", """{"score_value":1,"name":"left"}""");
        await right.AddItem("b", """{"score_value":1,"name":"right"}""");
        await (await fixture.Database.GetContainer("hidden")).AddItem("c", """{"score_value":1,"name":"hidden"}""");
        Check(await left.BuildQueryable<QueryableRow>().CountAsync() == 1 && await right.BuildQueryable<QueryableRow>().CountAsync() == 1,
            "Native queries lost container isolation.");
        try
        {
            await left.BuildQueryable<QueryableRow>().Join(right.BuildQueryable<QueryableRow>(),
                a => a.Score, b => b.Score, (a, b) => new { Left = a.Name, Right = b.Name }).ToListAsync();
            throw new Exception("Driver unexpectedly accepted a filtered join source; verify container isolation before enabling it.");
        }
        catch (MongoDB.Driver.Linq.ExpressionNotSupportedException) { }
    }

    [Test]
    public async Task Unsupported_code_cancellation_and_unloaded_queries_are_rejected()
    {
        await using MongoPersistenceFixture fixture = await MongoPersistenceFixture.Create();
        ILibrarianContainer items = await fixture.Database.GetContainer("items");
        await items.AddItem("a", """{"score_value":1,"name":"alpha"}""");
        IQueryable<QueryableRow> query = items.BuildQueryable<QueryableRow>();
        try { await query.Select(row => LocalOnly(row.Name!)).ToListAsync(); throw new Exception("Client projection executed."); }
        catch (MongoDB.Driver.Linq.ExpressionNotSupportedException) { }
        try { query.Take(0).Count(); throw new Exception("Driver unexpectedly accepted zero Take."); }
        catch (ArgumentOutOfRangeException) { }
        try { await query.CountAsync(new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        await fixture.Database.UnloadContainer("items");
        try { await query.ToListAsync(); throw new Exception("Unloaded query executed."); }
        catch (ObjectDisposedException) { }
    }

    private static string LocalOnly(string value) => new string(value.Reverse().ToArray());
}
