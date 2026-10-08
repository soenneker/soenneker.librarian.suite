using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class MongoNativeLinqTests
{
    [Test]
    public async Task Native_grouping_arrays_aggregates_and_unbounded_projections(CancellationToken cancellationToken)
    {
        await using var fixture = await MongoPersistenceFixture.Create();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("a", NativeDocumentJson.Create("a", """{"score_value":1,"name":"alpha","amount":1.25,"scores":[1,2],"details":{"region":"east"},"status":"Active","optional":3,"metrics":{"clicks":5},"stringNumber":"7"}"""), cancellationToken: cancellationToken);
        await items.AddItem("b", NativeDocumentJson.Create("b", """{"score_value":2,"name":"beta","amount":2.50,"scores":[2,3],"details":{"region":"east"},"status":"Pending"}"""), cancellationToken: cancellationToken);
        await items.AddItem("c", NativeDocumentJson.Create("c", """{"score_value":3,"name":"gamma","amount":3.75,"scores":[4],"details":{"region":"west"},"status":"Active"}"""), cancellationToken: cancellationToken);
        await (await fixture.Database.GetContainer("other", cancellationToken: cancellationToken)).AddItem("hidden", NativeDocumentJson.Create("hidden", """{"score_value":999,"name":"hidden"}"""), cancellationToken: cancellationToken);
        IQueryable<MongoLinqRow> query = items.BuildQueryable<MongoLinqRow>();

        var groups = await query.GroupBy(row => row.Details.Region)
            .Select(group => new { Region = group.Key, Count = group.Count(), Total = group.Sum(row => row.Amount) })
            .OrderBy(group => group.Region).ToListAsync(cancellationToken: cancellationToken);
        Check(groups.Count == 2 && groups[0].Count == 2 && groups[0].Total == 3.75m, "Server grouping/decimal sum failed.");
        Check(await query.ExecuteAsync(rows => rows.Sum(row => row.Amount), cancellationToken: cancellationToken) == 7.50m, "Async Sum failed.");
        Check(query.Average(row => row.Amount) == 2.50m && query.Max(row => row.Score) == 3, "Native aggregates failed.");
        Check(await query.Where(row => row.Scores.Any(score => score > 3)).CountAsync(cancellationToken: cancellationToken) == 1, "Nested Any failed.");
        Check((await query.SelectMany(row => row.Scores).Distinct().OrderBy(score => score).ToListAsync(cancellationToken: cancellationToken)).SequenceEqual([1, 2, 3, 4]), "SelectMany/Distinct failed.");
        Check(await query.CountAsync(row => row.Status == RedisStatus.Active && row.Optional.HasValue, cancellationToken: cancellationToken) == 1, "Enum/nullable query failed.");
        Check(await query.CountAsync(row => row.Metrics["clicks"] == 5, cancellationToken: cancellationToken) == 1, "Dictionary member query failed.");
        Check(await query.CountAsync(row => row.StringNumber == 7, cancellationToken: cancellationToken) == 1, "Member JSON number handling was lost.");
        var projected = await query.OrderBy(row => row.Details.Region).ThenByDescending(row => row.Score)
            .Select(row => new { Label = row.Name.ToUpper(), Score = row.Score + 10 })
            .Where(row => row.Score > 11).ToListAsync(cancellationToken: cancellationToken);
        Check(projected.Count == 2 && projected[0].Label == "BETA", "Computed unbounded projection or ThenBy failed.");
        Check(await query.Where(row => row.Name.Contains("amm")).SingleAsync(cancellationToken: cancellationToken) is { Score: 3, Amount: 3.75m, Scores.Count: 1 }, "Native document materialization failed.");
        Check(query.OrderBy(row => row.Score).Take(2).Where(row => row.Score > 1).Single().Name == "beta", "Filter after paging failed.");
        Check(query.Where(row => row.Score > 0).ToString()!.Contains("$match", StringComparison.Ordinal), "Query did not retain a native pipeline.");
        Check(await MongoDB.Driver.Linq.MongoQueryable.CountAsync(query, cancellationToken: cancellationToken) == 3, "Driver async extensions failed.");
        using IAsyncCursor<MongoLinqRow> cursor = await MongoDB.Driver.Linq.MongoQueryable.ToCursorAsync(query, cancellationToken: cancellationToken);
        Check(await cursor.MoveNextAsync(cancellationToken: cancellationToken) && cursor.Current.Count() == 3, "Driver cursor execution failed.");
        IQueryable<MongoLinqRow> filtered = query.Where(row => row.Score > 1);
        Check(await filtered.CountAsync(cancellationToken: cancellationToken) == 2 && ((MongoDB.Driver.Linq.IMongoQueryProvider)filtered.Provider).LoggedStages.Length > 0, "No server pipeline was executed.");
    }

    [Test]
    public async Task Unsupported_native_join_does_not_bypass_container_filters(CancellationToken cancellationToken)
    {
        await using var fixture = await MongoPersistenceFixture.Create();
        ILibrarianContainer left = await fixture.Database.GetContainer("left", cancellationToken: cancellationToken);
        ILibrarianContainer right = await fixture.Database.GetContainer("right", cancellationToken: cancellationToken);
        await left.AddItem("a", NativeDocumentJson.Create("a", """{"score_value":1,"name":"left"}"""), cancellationToken: cancellationToken);
        await right.AddItem("b", NativeDocumentJson.Create("b", """{"score_value":1,"name":"right"}"""), cancellationToken: cancellationToken);
        await (await fixture.Database.GetContainer("hidden", cancellationToken: cancellationToken)).AddItem("c", NativeDocumentJson.Create("c", """{"score_value":1,"name":"hidden"}"""), cancellationToken: cancellationToken);
        Check(await left.BuildQueryable<NativeDocument>().CountAsync(cancellationToken: cancellationToken) == 1 && await right.BuildQueryable<NativeDocument>().CountAsync(cancellationToken: cancellationToken) == 1,
            "Native queries lost container isolation.");
        try
        {
            await left.BuildQueryable<NativeDocument>().Join(right.BuildQueryable<NativeDocument>(),
                a => a.Score, b => b.Score, (a, b) => new { Left = a.Name, Right = b.Name }).ToListAsync(cancellationToken: cancellationToken);
            throw new Exception("Driver unexpectedly accepted a filtered join source; verify container isolation before enabling it.");
        }
        catch (MongoDB.Driver.Linq.ExpressionNotSupportedException) { }
    }

    [Test]
    public async Task Unsupported_code_cancellation_and_unloaded_queries_are_rejected(CancellationToken cancellationToken)
    {
        await using var fixture = await MongoPersistenceFixture.Create();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("a", NativeDocumentJson.Create("a", """{"score_value":1,"name":"alpha"}"""), cancellationToken: cancellationToken);
        IQueryable<NativeDocument> query = items.BuildQueryable<NativeDocument>();
        try { await query.Select(row => LocalOnly(row.Name!)).ToListAsync(cancellationToken: cancellationToken); throw new Exception("Client projection executed."); }
        catch (MongoDB.Driver.Linq.ExpressionNotSupportedException) { }
        try { query.Take(0).Count(); throw new Exception("Driver unexpectedly accepted zero Take."); }
        catch (ArgumentOutOfRangeException) { }
        try { await query.CountAsync(new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        await fixture.Database.UnloadContainer("items", cancellationToken: cancellationToken);
        try { await query.ToListAsync(cancellationToken: cancellationToken); throw new Exception("Unloaded query executed."); }
        catch (ObjectDisposedException) { }
    }

    private static string LocalOnly(string value) => new string(value.Reverse().ToArray());
}
