using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Shared query expression and enumeration adapter for Librarian query providers.</summary>
public sealed class LibrarianQueryable<T> : IOrderedQueryable<T>
{
    public LibrarianQueryable(IQueryProvider provider, Expression? expression = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        QueryTypes.Register<T>();
        Provider = provider;
        Expression = expression ?? Expression.Constant(this);
    }
    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider { get; }
    public IEnumerator<T> GetEnumerator() => (Provider is ILibrarianSequenceProvider sequence
        ? sequence.ExecuteSequence<T>(Expression) : Provider.Execute<IEnumerable<T>>(Expression)).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
