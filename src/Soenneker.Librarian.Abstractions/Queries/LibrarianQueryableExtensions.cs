using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Asynchronous terminal operations for Librarian queryables.</summary>
public static class LibrarianQueryableExtensions
{
    /// <summary>Materializes the query asynchronously, checking cancellation while collecting results.</summary>
    public static async ValueTask<List<T>> ToListAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        IEnumerable<T> source = await Provider(query).ExecuteAsync<IEnumerable<T>>(query.Expression, cancellationToken).ConfigureAwait(false);
        var results = new List<T>();
        foreach (T item in source) { cancellationToken.ThrowIfCancellationRequested(); results.Add(item); }
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>Materializes the query into an array asynchronously.</summary>
    public static async ValueTask<T[]> ToArrayAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) =>
        (await query.ToListAsync(cancellationToken).ConfigureAwait(false)).ToArray();

    /// <summary>Counts query results asynchronously.</summary>
    public static ValueTask<int> CountAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) => query.ExecuteAsync(q => q.Count(), cancellationToken);
    /// <summary>Counts matches asynchronously.</summary>
    public static ValueTask<int> CountAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) => query.Where(predicate).CountAsync(cancellationToken);
    /// <summary>Counts query results as a 64-bit integer asynchronously.</summary>
    public static ValueTask<long> LongCountAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) => query.ExecuteAsync(q => q.LongCount(), cancellationToken);
    /// <summary>Checks whether the query has any results asynchronously.</summary>
    public static ValueTask<bool> AnyAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) => query.ExecuteAsync(q => q.Any(), cancellationToken);
    /// <summary>Checks whether the predicate matches any results asynchronously.</summary>
    public static ValueTask<bool> AnyAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) => query.Where(predicate).AnyAsync(cancellationToken);
    /// <summary>Checks whether every query result matches a predicate asynchronously.</summary>
    public static ValueTask<bool> AllAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) =>
        Terminal<T, bool>(query, nameof(Queryable.All), predicate, cancellationToken);
    /// <summary>Returns the first query result asynchronously, throwing if empty.</summary>
    public static ValueTask<T> FirstAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) => query.ExecuteAsync(q => q.First(), cancellationToken);
    /// <summary>Returns the first matching result asynchronously, throwing if empty.</summary>
    public static ValueTask<T> FirstAsync<T>(this IQueryable<T> query, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) => query.Where(predicate).FirstAsync(cancellationToken);
    /// <summary>Returns the first result or its default value asynchronously.</summary>
    public static ValueTask<T?> FirstOrDefaultAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) => query.ExecuteAsync(q => q.FirstOrDefault(), cancellationToken);
    /// <summary>Returns the only result asynchronously, throwing unless exactly one result exists.</summary>
    public static ValueTask<T> SingleAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) => query.ExecuteAsync(q => q.Single(), cancellationToken);
    /// <summary>Returns the only result or its default asynchronously, throwing when multiple results exist.</summary>
    public static ValueTask<T?> SingleOrDefaultAsync<T>(this IQueryable<T> query, CancellationToken cancellationToken = default) => query.ExecuteAsync(q => q.SingleOrDefault(), cancellationToken);

    /// <summary>Executes a supported terminal expression asynchronously, including numeric aggregates.</summary>
    /// <example><code>await query.ExecuteAsync(q => q.Sum(row => row.Amount), cancellationToken);</code></example>
    /// <remarks>Only expressions accepted by the query's provider are allowed. This does not compile the expression for local fallback.</remarks>
    public static ValueTask<TResult> ExecuteAsync<T, TResult>(this IQueryable<T> query, Expression<Func<IQueryable<T>, TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ILibrarianAsyncQueryProvider provider = Provider(query);
        Expression expression = new Replace(operation.Parameters[0], query.Expression).Visit(operation.Body)!;
        return provider.ExecuteAsync<TResult>(expression, cancellationToken);
    }

    private static ValueTask<TResult> Terminal<T, TResult>(IQueryable<T> query, string name, Expression<Func<T, bool>> predicate, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return Provider(query).ExecuteAsync<TResult>(Expression.Call(typeof(Queryable), name, [typeof(T)], query.Expression, Expression.Quote(predicate)), token);
    }

    private static ILibrarianAsyncQueryProvider Provider<T>(IQueryable<T> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return query.Provider as ILibrarianAsyncQueryProvider ?? throw new NotSupportedException("The query provider does not support Librarian asynchronous execution.");
    }

    private sealed class Replace(Expression source, Expression replacement) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node) => node == source ? replacement : base.Visit(node);
    }
}
