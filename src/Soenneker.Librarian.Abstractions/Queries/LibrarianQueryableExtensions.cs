using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Asynchronous terminal operations for Librarian queryables.</summary>
public static class LibrarianQueryableExtensions
{
    /// <summary>Reads a native server page. Requires a provider implementing ILibrarianPagedQueryProvider.</summary>
    /// <remarks>Continue until ContinuationToken is null, including after an empty page. Keep the query and partition unchanged.</remarks>
    public static ValueTask<LibrarianPage<T>> ToPage<T>(this IQueryable<T> query, int pageSize = 100,
        string? continuationToken = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        return query.Provider is ILibrarianPagedQueryProvider provider
            ? provider.ReadPage<T>(query.Expression, pageSize, continuationToken, cancellationToken)
            : throw new NotSupportedException("This provider does not support continuation-token paging.");
    }

    /// <summary>Materializes the query asynchronously, checking cancellation while collecting results.</summary>
    public static async ValueTask<List<T>> ToList<T>(this IQueryable<T> query,
        CancellationToken cancellationToken)
    {
        IEnumerable<T> source = await Provider(query).Execute<IEnumerable<T>>(query.Expression, cancellationToken)
                                                     .NoSync();
        var results = new List<T>(source.TryGetNonEnumeratedCount(out int count) ? count : 0);
        foreach (T item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(item);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>Materializes the query into an array asynchronously.</summary>
    public static async ValueTask<T[]> ToArray<T>(this IQueryable<T> query,
        CancellationToken cancellationToken)
    {
        IEnumerable<T> source = await Provider(query).Execute<IEnumerable<T>>(query.Expression, cancellationToken).NoSync();
        cancellationToken.ThrowIfCancellationRequested();
        T[] result = (source is ICollection<T> || !cancellationToken.CanBeCanceled
            ? source : WithCancellation(source, cancellationToken)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static IEnumerable<T> WithCancellation<T>(IEnumerable<T> source, CancellationToken token)
    {
        foreach (T item in source)
        {
            token.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    /// <summary>Counts query results asynchronously.</summary>
    public static ValueTask<int>
        Count<T>(this IQueryable<T> query, CancellationToken cancellationToken) =>
        query.Execute(QueryTerminals<T>.Count, cancellationToken);

    /// <summary>Counts matches asynchronously.</summary>
    public static ValueTask<int> Count<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken) => query.Where(predicate).Count(cancellationToken);

    /// <summary>Counts query results as a 64-bit integer asynchronously.</summary>
    public static ValueTask<long> LongCount<T>(this IQueryable<T> query,
        CancellationToken cancellationToken) => query.Execute(QueryTerminals<T>.LongCount, cancellationToken);

    /// <summary>Checks whether the query has any results asynchronously.</summary>
    public static ValueTask<bool>
        Any<T>(this IQueryable<T> query, CancellationToken cancellationToken) =>
        query.Execute(QueryTerminals<T>.Any, cancellationToken);

    /// <summary>Checks whether the predicate matches any results asynchronously.</summary>
    public static ValueTask<bool> Any<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken) => query.Where(predicate).Any(cancellationToken);

    /// <summary>Checks whether every query result matches a predicate asynchronously.</summary>
    public static ValueTask<bool> All<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken) =>
        ExecuteAll(query, predicate, cancellationToken);

    /// <summary>Returns the first query result asynchronously, throwing if empty.</summary>
    public static ValueTask<T> First<T>(this IQueryable<T> query, CancellationToken cancellationToken) =>
        query.Execute(QueryTerminals<T>.First, cancellationToken);

    /// <summary>Returns the first matching result asynchronously, throwing if empty.</summary>
    public static ValueTask<T> First<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken) => query.Where(predicate).First(cancellationToken);

    /// <summary>Returns the first result or its default value asynchronously.</summary>
    public static ValueTask<T?> FirstOrDefault<T>(this IQueryable<T> query,
        CancellationToken cancellationToken) =>
        query.Execute(QueryTerminals<T>.FirstOrDefault, cancellationToken);

    /// <summary>Returns the only result asynchronously, throwing unless exactly one result exists.</summary>
    public static ValueTask<T>
        Single<T>(this IQueryable<T> query, CancellationToken cancellationToken) =>
        query.Execute(QueryTerminals<T>.Single, cancellationToken);

    /// <summary>Returns the only result or its default asynchronously, throwing when multiple results exist.</summary>
    public static ValueTask<T?> SingleOrDefault<T>(this IQueryable<T> query,
        CancellationToken cancellationToken) =>
        query.Execute(QueryTerminals<T>.SingleOrDefault, cancellationToken);

    /// <summary>Executes a supported terminal expression asynchronously, including numeric aggregates.</summary>
    /// <example><code>await query.Execute(q => q.Sum(row => row.Amount), cancellationToken);</code></example>
    /// <remarks>Only expressions accepted by the query's provider are allowed. This does not compile the expression for local fallback.</remarks>
    public static ValueTask<TResult> Execute<T, TResult>(this IQueryable<T> query,
        Expression<Func<IQueryable<T>, TResult>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ILibrarianAsyncQueryProvider provider = Provider(query);
        QueryTypes.Register<TResult>();
        Expression expression = new QueryExpressionReplacement(operation.Parameters[0], query.Expression).Visit(operation.Body)!;
        return provider.Execute<TResult>(expression, cancellationToken);
    }

    private static ValueTask<bool> ExecuteAll<T>(IQueryable<T> query, Expression<Func<T, bool>> predicate,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        var call = (MethodCallExpression)QueryTerminals<T>.All.Body;
        return Provider(query)
            .Execute<bool>(call.Update(null, [query.Expression, Expression.Quote(predicate)]), token);
    }

    private static ILibrarianAsyncQueryProvider Provider<T>(IQueryable<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Provider as ILibrarianAsyncQueryProvider ??
               throw new NotSupportedException("The query provider does not support Librarian asynchronous execution.");
    }
}
