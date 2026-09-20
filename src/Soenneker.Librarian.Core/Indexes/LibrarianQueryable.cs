using Soenneker.Librarian.Abstractions.Queries;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Core.Indexes;

internal sealed class LibrarianQueryable<T> : IOrderedQueryable<T>
{
    public LibrarianQueryable(IQueryProvider provider, Expression? expression = null)
    {
        QueryTypes.Register<T>();
        Provider = provider;
        Expression = expression ?? Expression.Constant(this);
    }

    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider { get; }
    public IEnumerator<T> GetEnumerator() => ((ILibrarianSequenceProvider)Provider).ExecuteSequence<T>(Expression).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

