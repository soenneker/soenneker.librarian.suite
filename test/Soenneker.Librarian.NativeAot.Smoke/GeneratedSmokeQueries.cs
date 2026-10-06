using System.Linq.Expressions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Postgres;
using Soenneker.Librarian.Redis;

internal static class GeneratedSmokeQueries
{
    internal static void Run()
    {
        var provider = new RedisQueryProvider<SmokeRow>(null!);
        IQueryable<SmokeRow> root = new LibrarianQueryable<SmokeRow>(provider);
        var query = root.Where(row => row.Score >= 2).Take(2).Select(row => new SmokeProjection(row.Name, row.Score));
        var lambda = (LambdaExpression)((UnaryExpression)((MethodCallExpression)query.Expression).Arguments[1]).Operand;
        if (!GeneratedQueryFunctions.TryGet(lambda, out var function) ||
            function(new SmokeRow { Name = "generated", Score = 3 }, 0) is not SmokeProjection { Name: "generated", Score: 3 })
            throw new InvalidOperationException("Generated query delegate failed.");
        if (RedisQueryPlan.Create(query.Expression, provider).Projection!.Materialize(["\"generated\"", "3"]) is not SmokeProjection { Name: "generated", Score: 3 })
            throw new InvalidOperationException("Generated Redis projection failed.");
        if (PostgresQueryPlan.Create(query.Expression, provider).Projection!.Materialize(["\"generated\"", "3"]) is not SmokeProjection { Name: "generated", Score: 3 })
            throw new InvalidOperationException("Generated PostgreSQL projection failed.");
        if (!GeneratedQueryMetadata.TryGet(typeof(SmokeRow), "Score", out var metadata) || metadata.IndexKey!(new SmokeRow { Score = 3 }).Number != 3)
            throw new InvalidOperationException("Generated index metadata failed.");
    }
}
