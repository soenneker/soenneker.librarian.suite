using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Cosmos;

// This adapts asynchronous terminal execution only. The Cosmos SDK translates the entire query expression.
internal sealed class CosmosQueryable<T>
    : IOrderedQueryable<T>, ILibrarianAsyncQueryProvider, ILibrarianPagedQueryProvider
{
    private readonly IQueryable<T> _native;
    private readonly Action _check;
    private readonly Container _store;
    private readonly string _scope;
    private readonly CosmosLibrarianDatabase _database;
    private readonly string _name;
    private readonly string? _partition;

    internal CosmosQueryable(IQueryable<T> native, Action check, Container store, string scope, CosmosLibrarianDatabase database, string name, string? partition)
    {
        QueryTypes.Register<T>();
        _native = native;
        _check = check;
        _store = store;
        _scope = scope;
        _database = database;
        _name = name;
        _partition = partition;
    }

    public Type ElementType => typeof(T);
    public Expression Expression => _native.Expression;
    public IQueryProvider Provider => this;
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new CosmosQueryable<TElement>(_native.Provider.CreateQuery<TElement>(expression), _check, _store, _scope, _database, _name, _partition);
    public IQueryable CreateQuery(Expression expression) => QueryTypes.CreateQuery(this, expression);

    public IEnumerator<T> GetEnumerator() => throw new NotSupportedException("Synchronous query execution is not supported by this provider. Use Librarian asynchronous query operations.");
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    object? IQueryProvider.Execute(Expression expression) => throw new NotSupportedException("Synchronous query execution is not supported by this provider. Use Librarian asynchronous query operations.");
    TResult IQueryProvider.Execute<TResult>(Expression expression) => throw new NotSupportedException("Synchronous query execution is not supported by this provider. Use Librarian asynchronous query operations.");

    public async ValueTask<LibrarianPage<TElement>> ReadPage<TElement>(Expression expression, int pageSize, string? continuationToken = null,
        CancellationToken cancellationToken = default)
    {
        _check();
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        QueryDefinition query = _native.Provider.CreateQuery<TElement>(expression).ToQueryDefinition() ?? new QueryDefinition("SELECT VALUE c FROM c");
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new CosmosQueryFingerprint(
            _scope, _store.Database.Id, _store.Id, typeof(TElement).FullName, query.QueryText,
            query.GetQueryParameters()?.Select(parameter => new CosmosQueryParameter(parameter.Name, LibrarianJson.Element(parameter.Value))).ToArray()),
            CosmosInternalJsonContext.Default.CosmosQueryFingerprint))));
        string? token = null;
        if (continuationToken is not null)
        {
            try
            {
                CosmosContinuation saved = JsonSerializer.Deserialize(Convert.FromBase64String(continuationToken), CosmosInternalJsonContext.Default.CosmosContinuation)
                    ?? throw new FormatException();
                if (saved.Query != fingerprint || string.IsNullOrEmpty(saved.Token)) throw new FormatException();
                token = saved.Token;
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
                throw new ArgumentException("The continuation token is invalid or belongs to a different query or partition.", nameof(continuationToken), exception);
            }
        }
        QueryRequestOptions options = _database.QueryOptions(_name, _partition);
        options.MaxItemCount = pageSize;
        using FeedIterator<TElement> iterator = _store.GetItemQueryIterator<TElement>(query, token, options);
        FeedResponse<TElement> page = await iterator.ReadNextAsync(cancellationToken).NoSync();
        _database.Observe(_name, "QueryPage", page.StatusCode, page.Headers, page.Diagnostics);
        string? next = string.IsNullOrEmpty(page.ContinuationToken) ? null
            : Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new CosmosContinuation(fingerprint, page.ContinuationToken), CosmosInternalJsonContext.Default.CosmosContinuation));
        return new LibrarianPage<TElement>(page.ToList(), next);
    }

    public async ValueTask<TResult> Execute<TResult>(Expression expression, CancellationToken cancellationToken = default)
    {
        _check();
        cancellationToken.ThrowIfCancellationRequested();
        if (typeof(IQueryable).IsAssignableFrom(expression.Type))
            return (TResult)(object)await Read(_native.Provider.CreateQuery<T>(expression), cancellationToken).NoSync();
        // Cosmos translates Count, but not LongCount. Materialize its _native COUNT result directly as Int64.
        if (expression is MethodCallExpression { Method.Name: nameof(Queryable.LongCount) } longCount && longCount.Method.DeclaringType == typeof(Queryable))
            expression = Expression.Call(longCount.Arguments.Count == 1
                ? ((Func<IQueryable<T>, int>)Queryable.Count<T>).Method
                : ((Func<IQueryable<T>, Expression<Func<T, bool>>, int>)Queryable.Count<T>).Method, longCount.Arguments.ToArray());
        if (expression is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable) &&
            call.Method.Name is nameof(Queryable.First) or nameof(Queryable.FirstOrDefault) or nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault) or nameof(Queryable.Any) or nameof(Queryable.All))
        {
            IQueryable<T> source = _native.Provider.CreateQuery<T>(call.Arguments[0]);
            string terminal = call.Method.Name;
            if (call.Arguments.Count == 2)
            {
                if (call.Arguments[1] is not UnaryExpression { Operand: Expression<Func<T, bool>> predicate })
                    throw new NotSupportedException("This terminal overload is not supported.");
                if (terminal == nameof(Queryable.All)) predicate = Expression.Lambda<Func<T, bool>>(Expression.Not(predicate.Body), predicate.Parameters);
                source = source.Where(predicate);
            }
            List<T> items = await Read(source.Take(terminal is nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault) ? 2 : 1), cancellationToken).NoSync();
            if (terminal == nameof(Queryable.Any)) return (TResult)(object)(items.Count != 0);
            if (terminal == nameof(Queryable.All)) return (TResult)(object)(items.Count == 0);
            if (items.Count > 1) throw new InvalidOperationException("Sequence contains more than one element");
            if (items.Count > 0) return (TResult)(object)items[0]!;
            if (terminal is nameof(Queryable.First) or nameof(Queryable.Single)) throw new InvalidOperationException("Sequence contains no elements");
            return default!;
        }
        List<TResult> result = await Read(_native.Provider.CreateQuery<TResult>(expression), cancellationToken).NoSync();
        return result.Single();
    }
    private async ValueTask<List<TElement>> Read<TElement>(IQueryable<TElement> query, CancellationToken token)
    {
        using FeedIterator<TElement> iterator = _store.GetItemQueryIterator<TElement>(query.ToQueryDefinition(), requestOptions: _database.QueryOptions(_name, _partition));
        var results = new List<TElement>();
        while (iterator.HasMoreResults)
        {
            FeedResponse<TElement> page = await iterator.ReadNextAsync(token).NoSync();
            _database.Observe(_name, "Query", page.StatusCode, page.Headers, page.Diagnostics);
            results.AddRange(page);
        }
        return results;
    }
}
