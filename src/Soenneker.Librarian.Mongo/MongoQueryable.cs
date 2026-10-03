using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Mongo;

// Only adapts lifetime checks and asynchronous execution; the driver translates every expression.
internal sealed class MongoQueryable<T>(IQueryable<T> native, Func<CancellationToken, ValueTask> prepare)
    : IOrderedQueryable<T>, ILibrarianAsyncQueryProvider, IMongoQueryProvider, IAsyncCursorSource<T>
{
    public BsonDocument[] LoggedStages => ((IMongoQueryProvider)native.Provider).LoggedStages;
    public Type ElementType => typeof(T);
    public Expression Expression => native.Expression;
    public IQueryProvider Provider => this;
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
        new MongoQueryable<TElement>(native.Provider.CreateQuery<TElement>(expression), prepare);
    public IQueryable CreateQuery(Expression expression)
    {
        IQueryable query = native.Provider.CreateQuery(expression);
        return (IQueryable)Activator.CreateInstance(typeof(MongoQueryable<>).MakeGenericType(query.ElementType), query, prepare)!;
    }
    public IEnumerator<T> GetEnumerator()
    {
        prepare(default).AsTask().GetAwaiter().GetResult();
        return native.GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public object? Execute(Expression expression)
    {
        prepare(default).AsTask().GetAwaiter().GetResult();
        return native.Provider.Execute(expression);
    }
    public TResult Execute<TResult>(Expression expression)
    {
        prepare(default).AsTask().GetAwaiter().GetResult();
        return native.Provider.Execute<TResult>(expression);
    }
    public async ValueTask<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        await prepare(cancellationToken).ConfigureAwait(false);
        if (typeof(IQueryable).IsAssignableFrom(expression.Type))
        {
            using IAsyncCursor<T> cursor = await native.Provider.CreateQuery<T>(expression).ToCursorAsync(cancellationToken).ConfigureAwait(false);
            return (TResult)(object)await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        return await ((IMongoQueryProvider)native.Provider).ExecuteAsync<TResult>(expression, cancellationToken).ConfigureAwait(false);
    }
    Task<TResult> IMongoQueryProvider.ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken) =>
        ExecuteAsync<TResult>(expression, cancellationToken).AsTask();
    public IAsyncCursor<T> ToCursor(CancellationToken cancellationToken = default)
    {
        prepare(cancellationToken).AsTask().GetAwaiter().GetResult();
        return native.ToCursor(cancellationToken);
    }
    public async Task<IAsyncCursor<T>> ToCursorAsync(CancellationToken cancellationToken = default)
    {
        await prepare(cancellationToken).ConfigureAwait(false);
        return await native.ToCursorAsync(cancellationToken).ConfigureAwait(false);
    }
    public override string ToString() => native.ToString()!;
}
