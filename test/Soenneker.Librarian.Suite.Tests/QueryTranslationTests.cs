using System;
using System.Linq;
using System.Linq.Expressions;
using Soenneker.Librarian.Postgres;
using Soenneker.Librarian.Redis;

namespace Soenneker.Librarian.Suite.Tests;

public class QueryTranslationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void Membership_captured_properties_and_bounded_projections_translate(bool postgres)
    {
        IQueryable<PostgresRow> root = Array.Empty<PostgresRow>().AsQueryable();
        decimal[] amounts = [1, 2, 3];
        var settings = new { Prefix = "A", Limit = 2 };
        IQueryable<string?> query = root.Where(row => amounts.Contains(row.Amount) && row.Name!.StartsWith(settings.Prefix, StringComparison.Ordinal))
            .OrderBy(row => row.Amount).Take(settings.Limit).Select(row => row.Name);
        object? result = postgres
            ? PostgresQueryPlan.Create(query.Expression, root.Provider).Projection!.Materialize(["\"Alpha\""])
            : RedisQueryPlan.Create(query.Expression, root.Provider).Projection!.Materialize(["\"Alpha\""]);
        if (!Equals(result, "Alpha")) throw new Exception("Bounded projection changed.");
        Translate(root.Where(row => new[] { 1m, 2m }.Contains(row.Amount)), root.Provider, postgres);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void Unsupported_expressions_keep_provider_errors(bool postgres)
    {
        IQueryable<PostgresRow> root = Array.Empty<PostgresRow>().AsQueryable();
        Reject(() => Translate(root.Where(row => row.Name!.StartsWith("A", StringComparison.OrdinalIgnoreCase)), root.Provider, postgres), postgres);
        Reject(() => Translate(root.Where(row => row.Name!.ToLower() == "a"), root.Provider, postgres), postgres);
        Reject(() => Translate(root.OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase), root.Provider, postgres), postgres);
    }

    [Test]
    public void Provider_specific_composition_rules_remain_distinct()
    {
        IQueryable<PostgresRow> root = Array.Empty<PostgresRow>().AsQueryable();
        IQueryable<PostgresRow> query = root.Take(4).Where(row => row.Amount > 1).OrderBy(row => row.Amount).ThenBy(row => row.Name);
        Translate(query, root.Provider, postgres: true);
        Reject(() => Translate(query, root.Provider, postgres: false), postgres: false);
        Translate(root.Select(row => row.Amount + 1).Distinct(), root.Provider, postgres: true);
        Reject(() => Translate(root.Select(row => row.Name), root.Provider, postgres: false), postgres: false);
        Translate(root.Where(row => row.Amount + 1 > row.Amount), root.Provider, postgres: true);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public void Predicate_terminals_translate_without_executing_the_query(bool postgres)
    {
        IQueryable<PostgresRow> root = Array.Empty<PostgresRow>().AsQueryable();
        Expression<Func<IQueryable<PostgresRow>, bool>> all = query => query.All(row => row.Active);
        var call = (MethodCallExpression)all.Body;
        Expression terminal = call.Update(null, [root.Expression, call.Arguments[1]]);
        if (postgres) PostgresQueryPlan.Create(terminal, root.Provider);
        else RedisQueryPlan.Create(terminal, root.Provider);
    }

    private static void Translate(IQueryable query, IQueryProvider owner, bool postgres)
    {
        if (postgres) PostgresQueryPlan.Create(query.Expression, owner);
        else RedisQueryPlan.Create(query.Expression, owner);
    }

    private static void Reject(Action action, bool postgres)
    {
        try { action(); }
        catch (NotSupportedException exception)
        {
            if (!exception.Message.Contains(postgres ? "PostgreSQL" : "Redis", StringComparison.Ordinal))
                throw new Exception("Provider-specific error was lost.", exception);
            return;
        }
        throw new Exception("Unsupported query was accepted.");
    }
}
