using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Postgres;

internal sealed class PostgresQueryable<T> : IOrderedQueryable<T>
{
    public PostgresQueryable(IQueryProvider provider, Expression? expression = null)
    {
        Provider = provider;
        Expression = expression ?? Expression.Constant(this);
    }
    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider { get; }
    public IEnumerator<T> GetEnumerator() => Provider.Execute<IEnumerable<T>>(Expression).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
