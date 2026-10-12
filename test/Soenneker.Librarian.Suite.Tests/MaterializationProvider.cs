using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class MaterializationProvider<T>(Func<IEnumerable<T>> source) : ILibrarianAsyncQueryProvider
{
    public ValueTask<TResult> Execute<TResult>(Expression expression, CancellationToken cancellationToken = default)
        => ValueTask.FromResult((TResult)(object)source());
    public IQueryable CreateQuery(Expression expression) => throw new NotSupportedException();
    public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => throw new NotSupportedException();
    object? IQueryProvider.Execute(Expression expression) => throw new NotSupportedException();
    TResult IQueryProvider.Execute<TResult>(Expression expression) => throw new NotSupportedException();
}
