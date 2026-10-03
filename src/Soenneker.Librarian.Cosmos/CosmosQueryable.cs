using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Cosmos;

// This adapts asynchronous terminal execution only. The Cosmos SDK translates the entire query expression.
internal sealed class CosmosQueryable<T>(IQueryable<T> native, Action check) : IOrderedQueryable<T>, ILibrarianAsyncQueryProvider
{
    public Type ElementType => typeof(T);
    public Expression Expression => native.Expression;
    public IQueryProvider Provider => this;
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new CosmosQueryable<TElement>(native.Provider.CreateQuery<TElement>(expression), check);
    public IQueryable CreateQuery(Expression expression)
    {
        IQueryable query = native.Provider.CreateQuery(expression);
        return (IQueryable)Activator.CreateInstance(typeof(CosmosQueryable<>).MakeGenericType(query.ElementType), query, check)!;
    }
    public IEnumerator<T> GetEnumerator() => Execute<IEnumerable<T>>(Expression).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public object? Execute(Expression expression) { check(); return native.Provider.Execute(expression); }
    public TResult Execute<TResult>(Expression expression) => ExecuteAsync<TResult>(expression).AsTask().GetAwaiter().GetResult();

    public async ValueTask<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        check();
        cancellationToken.ThrowIfCancellationRequested();
        if (typeof(IQueryable).IsAssignableFrom(expression.Type))
            return (TResult)(object)await Read(native.Provider.CreateQuery<T>(expression), cancellationToken).ConfigureAwait(false);
        // Cosmos translates Count, but not LongCount. Materialize its native COUNT result directly as Int64.
        if (expression is MethodCallExpression { Method.Name: nameof(Queryable.LongCount) } longCount && longCount.Method.DeclaringType == typeof(Queryable))
            expression = Expression.Call(typeof(Queryable), nameof(Queryable.Count), [typeof(T)], longCount.Arguments.ToArray());
        if (expression is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable) &&
            call.Method.Name is nameof(Queryable.First) or nameof(Queryable.FirstOrDefault) or nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault) or nameof(Queryable.Any) or nameof(Queryable.All))
        {
            IQueryable<T> source = native.Provider.CreateQuery<T>(call.Arguments[0]);
            string terminal = call.Method.Name;
            if (call.Arguments.Count == 2)
            {
                if (call.Arguments[1] is not UnaryExpression { Operand: Expression<Func<T, bool>> predicate })
                    throw new NotSupportedException("This terminal overload is not supported.");
                if (terminal == nameof(Queryable.All)) predicate = Expression.Lambda<Func<T, bool>>(Expression.Not(predicate.Body), predicate.Parameters);
                source = source.Where(predicate);
            }
            List<T> items = await Read(source.Take(terminal is nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault) ? 2 : 1), cancellationToken).ConfigureAwait(false);
            if (terminal == nameof(Queryable.Any)) return (TResult)(object)(items.Count != 0);
            if (terminal == nameof(Queryable.All)) return (TResult)(object)(items.Count == 0);
            if (items.Count > 1) throw new InvalidOperationException("Sequence contains more than one element");
            if (items.Count > 0) return (TResult)(object)items[0]!;
            if (terminal is nameof(Queryable.First) or nameof(Queryable.Single)) throw new InvalidOperationException("Sequence contains no elements");
            return default!;
        }
        List<TResult> result = await Read(native.Provider.CreateQuery<TResult>(expression), cancellationToken).ConfigureAwait(false);
        return result.Single();
    }
    private static async ValueTask<List<TElement>> Read<TElement>(IQueryable<TElement> query, CancellationToken token)
    {
        using FeedIterator<TElement> iterator = query.ToFeedIterator();
        var results = new List<TElement>();
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync(token).ConfigureAwait(false));
        return results;
    }
}
