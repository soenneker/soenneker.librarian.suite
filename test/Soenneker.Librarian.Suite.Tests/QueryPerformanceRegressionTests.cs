using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Memory;
using Soenneker.Librarian.Postgres;
using Soenneker.Librarian.Redis;

namespace Soenneker.Librarian.Suite.Tests;

public class QueryPerformanceRegressionTests
{
    [Test]
    public async Task Reused_fallback_query_observes_live_closures()
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        var container = await database.GetContainer("rows");
        for (int i = 0; i < 5; i++) await container.AddItem(i.ToString(), "{\"score\":" + i + "}");
        int direction = 1;
        var query = container.BuildQueryable<ConformanceRow>().Take(5).OrderBy(row => row.Score * direction);
        if (!query.Select(row => row.Score).ToArray().SequenceEqual(new[] { 0, 1, 2, 3, 4 })) throw new Exception("Initial order differs");
        direction = -1;
        if (!query.Select(row => row.Score).ToArray().SequenceEqual(new[] { 4, 3, 2, 1, 0 })) throw new Exception("Cached delegate froze a captured value");
    }

    [Test]
    public async Task Typed_parameters_bind_values_without_capturing_local_fields()
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        var container = await database.GetContainer("rows");
        for (int i = 0; i < 5; i++) await container.AddItem(i.ToString(), "{\"score\":" + i + "}");
        int minimum = 2;
        var query = container.BuildQueryable<ConformanceRow>().Where(minimum, (row, bound) => row.Score >= bound).OrderBy(row => row.Score);
        minimum = 4;
        if (!query.Select(row => row.Score).ToArray().SequenceEqual(new[] { 2, 3, 4 })) throw new Exception("Bound value changed");
        if (!query.Select(10, (row, offset) => row.Score + offset).ToArray().SequenceEqual(new[] { 12, 13, 14 })) throw new Exception("Typed projection differs");
    }

    [Test]
    public void Scalar_materializers_preserve_missing_defaults_and_nulls()
    {
        var redisProvider = new RedisQueryProvider<ConformanceRow>(null!);
        var redis = new LibrarianQueryable<ConformanceRow>(redisProvider);
        var postgresProvider = new PostgresQueryProvider<ConformanceRow>(null!);
        var postgres = new LibrarianQueryable<ConformanceRow>(postgresProvider);
        var redisNumber = RedisQueryPlan.Create(redis.Take(1).Select(row => row.Score).Expression, redisProvider).Projection!;
        var postgresNumber = PostgresQueryPlan.Create(postgres.Take(1).Select(row => row.Score).Expression, postgresProvider).Projection!;
        if (!Equals(redisNumber.Materialize([null]), 0) || !Equals(postgresNumber.Materialize([null]), 0)) throw new Exception("Missing numeric default differs");
        if (!Equals(redisNumber.Materialize(["123"]), 123) || !Equals(postgresNumber.Materialize(["123"]), 123)) throw new Exception("Numeric materialization differs");
        var redisText = RedisQueryPlan.Create(redis.Take(1).Select(row => row.Name).Expression, redisProvider).Projection!;
        var postgresText = PostgresQueryPlan.Create(postgres.Take(1).Select(row => row.Name).Expression, postgresProvider).Projection!;
        if (redisText.Materialize([null]) is not null || postgresText.Materialize([null]) is not null || redisText.Materialize(["null"]) is not null || postgresText.Materialize(["null"]) is not null) throw new Exception("Null reference default differs");
    }
}
