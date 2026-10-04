using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Postgres;
using Soenneker.Librarian.Redis;

internal static class ProviderQueryBenchmarks
{
    internal static void Run()
    {
        var redisProvider = new RedisQueryProvider<Row>(null!);
        var redis = new LibrarianQueryable<Row>(redisProvider);
        var postgresProvider = new PostgresQueryProvider<Row>(null!);
        var postgres = new LibrarianQueryable<Row>(postgresProvider);
        var redisQuery = redis.Where(row => row.Score > 100).OrderBy(row => row.Score).Take(10).Select(row => row.Score);
        var postgresQuery = postgres.Where(row => row.Score > 100).OrderBy(row => row.Score).Take(10).Select(row => row.Score);
        string?[] values = ["123"];
        if (!Equals(RedisQueryPlan.Create(redisQuery.Expression, redisProvider).Projection!.Materialize(values), 123)
            || !Equals(PostgresQueryPlan.Create(postgresQuery.Expression, postgresProvider).Projection!.Materialize(values), 123))
            throw new Exception("Provider scalar projection mismatch");
        Console.WriteLine("Operation,MedianMicroseconds,BytesPerOperation");
        AuditBenchmarks.Measure("Redis-plan-scalar-projection", _ => (int)RedisQueryPlan.Create(redisQuery.Expression, redisProvider).Projection!.Materialize(values)!, 2000);
        AuditBenchmarks.Measure("Postgres-plan-scalar-projection", _ => (int)PostgresQueryPlan.Create(postgresQuery.Expression, postgresProvider).Projection!.Materialize(values)!, 2000);
        var projection = RedisQueryPlan.Create(redisQuery.Expression, redisProvider).Projection!;
        AuditBenchmarks.Measure("Redis-materialize-scalar", _ => (int)projection.FromDocument("{\"score\":123}")!, 10000);
    }
}
