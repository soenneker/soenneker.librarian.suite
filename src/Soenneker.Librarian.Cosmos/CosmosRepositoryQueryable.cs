using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Cosmos;

// Carries legacy repository page defaults through LINQ composition without executing the query.
internal sealed class CosmosRepositoryQueryable<T>(IQueryable<T> query, int pageSize, string? continuationToken)
    : IOrderedQueryable<T>, ILibrarianAsyncQueryProvider, ILibrarianPagedQueryProvider, ICosmosPageReader
{
    private readonly int _pageSize = pageSize > 0 ? pageSize : throw new ArgumentOutOfRangeException(nameof(pageSize));
    internal int PageSize => _pageSize;
    internal string? ContinuationToken => continuationToken;
    public Type ElementType => typeof(T);
    public Expression Expression => query.Expression;
    public IQueryProvider Provider => this;
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
        new CosmosRepositoryQueryable<TElement>(query.Provider.CreateQuery<TElement>(expression), pageSize, continuationToken);
    public IQueryable CreateQuery(Expression expression) => QueryTypes.CreateQuery(this, expression);
    public IEnumerator<T> GetEnumerator() => throw new NotSupportedException("Use asynchronous Librarian query operations.");
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    object? IQueryProvider.Execute(Expression expression) => throw new NotSupportedException("Use asynchronous Librarian query operations.");
    TResult IQueryProvider.Execute<TResult>(Expression expression) => throw new NotSupportedException("Use asynchronous Librarian query operations.");
    public ValueTask<TResult> Execute<TResult>(Expression expression, CancellationToken cancellationToken) =>
        ((ILibrarianAsyncQueryProvider)query.Provider).Execute<TResult>(expression, cancellationToken);
    public ValueTask<LibrarianPage<TElement>> ReadPage<TElement>(Expression expression, int size, string? token = null,
        CancellationToken cancellationToken = default) =>
        ((ILibrarianPagedQueryProvider)query.Provider).ReadPage<TElement>(expression, size, token, cancellationToken);
    public ValueTask ReadPages<TElement>(Expression expression, int size, string? token,
        Func<List<TElement>, ValueTask> consume, CancellationToken cancellationToken) =>
        ((ICosmosPageReader)query.Provider).ReadPages(expression, size, token, consume, cancellationToken);
}
