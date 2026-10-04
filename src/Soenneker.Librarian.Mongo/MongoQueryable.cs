using Soenneker.Extensions.Task;
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
internal sealed class MongoQueryable<T>
    : IOrderedQueryable<T>, ILibrarianAsyncQueryProvider, IMongoQueryProvider, IAsyncCursorSource<T>
{
    private readonly IQueryable<T> _native;
    private readonly Action<CancellationToken> _check;

    internal MongoQueryable(IQueryable<T> native, Action<CancellationToken> check)
    {
        QueryTypes.Register<T>();
        _native = native;
        _check = check;
    }

    public BsonDocument[] LoggedStages => ((IMongoQueryProvider)_native.Provider).LoggedStages;
    public Type ElementType => typeof(T);
    public Expression Expression => _native.Expression;
    public IQueryProvider Provider => this;
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
        new MongoQueryable<TElement>(_native.Provider.CreateQuery<TElement>(expression), _check);
    public IQueryable CreateQuery(Expression expression) => QueryTypes.CreateQuery(this, expression);

    public IEnumerator<T> GetEnumerator()
    {
        _check(default);
        return _native.GetEnumerator();
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public object? Execute(Expression expression)
    {
        _check(default);
        return _native.Provider.Execute(expression);
    }
    public TResult Execute<TResult>(Expression expression)
    {
        _check(default);
        return _native.Provider.Execute<TResult>(expression);
    }
    public async ValueTask<TResult> ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        _check(cancellationToken);
        if (typeof(IQueryable).IsAssignableFrom(expression.Type))
        {
            using IAsyncCursor<T> cursor = await _native.Provider.CreateQuery<T>(expression).ToCursorAsync(cancellationToken).NoSync();
            return (TResult)(object)await cursor.ToListAsync(cancellationToken).NoSync();
        }
        return await ((IMongoQueryProvider)_native.Provider).ExecuteAsync<TResult>(expression, cancellationToken).NoSync();
    }
    Task<TResult> IMongoQueryProvider.ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken) =>
        ExecuteAsync<TResult>(expression, cancellationToken).AsTask();
    public IAsyncCursor<T> ToCursor(CancellationToken cancellationToken = default)
    {
        _check(cancellationToken);
        return _native.ToCursor(cancellationToken);
    }
    public Task<IAsyncCursor<T>> ToCursorAsync(CancellationToken cancellationToken = default)
    {
        _check(cancellationToken);
        return _native.ToCursorAsync(cancellationToken);
    }
    public override string ToString() => _native.ToString()!;
}
