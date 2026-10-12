using System;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Memory;
using Soenneker.Librarian.Postgres;
using Soenneker.Librarian.Redis;

namespace Soenneker.Librarian.Suite.Tests;

public class QueryExecutionTests
{
    [Test]
    public async ValueTask Local_execution_supports_named_sync_and_async_methods(CancellationToken cancellationToken)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        var container = await database.GetContainer("execution", cancellationToken: cancellationToken);
        await container.AddItem("one", "{\"score_value\":1}", cancellationToken: cancellationToken);
        IQueryable<QueryableRow> query = container.BuildQueryable<QueryableRow>();
        Expression expression = Expression.Call(typeof(Queryable), nameof(Queryable.Count), [typeof(QueryableRow)], query.Expression);
        var sync = (ILibrarianSyncQueryProvider)query.Provider;
        var asyncProvider = (ILibrarianAsyncQueryProvider)query.Provider;
        if (sync.ExecuteSync<int>(expression) != 1 || !Equals(sync.ExecuteSync(expression), 1)
            || await asyncProvider.Execute<int>(expression) != 1
            || await asyncProvider.Execute<int>(expression, cancellationToken) != 1
            || query.Count() != 1)
            throw new Exception("Sync and async execution produced different results.");
        try
        {
            await asyncProvider.Execute<int>(expression, new CancellationToken(true));
            throw new Exception("Async execution ignored cancellation.");
        }
        catch (OperationCanceledException) { }
    }

    [Test]
    public void Async_only_providers_reject_sync_execution_before_accessing_storage()
    {
        IQueryProvider[] providers = [new PostgresQueryProvider<QueryableRow>(null!), new RedisQueryProvider<QueryableRow>(null!)];
        foreach (IQueryProvider provider in providers)
        {
            if (provider is ILibrarianSyncQueryProvider) throw new Exception("Async-only provider advertises sync execution.");
            var query = new LibrarianQueryable<QueryableRow>(provider);
            Reject(() => provider.Execute(query.Expression));
            Reject(() => provider.Execute<int>(query.Expression));
            Reject(() => query.Count());
            Reject(() => query.ToArray());
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (NotSupportedException) { return; }
        throw new Exception("Async-only provider accepted synchronous execution.");
    }
}
