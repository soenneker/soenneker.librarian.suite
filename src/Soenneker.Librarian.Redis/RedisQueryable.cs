using Soenneker.Librarian.Abstractions.Queries;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Redis;

internal sealed class RedisQueryable<T> : IOrderedQueryable<T>
{
    public RedisQueryable(IQueryProvider provider, Expression? expression = null)
    {
        QueryTypes.Register<T>();
        Provider = provider;
        Expression = expression ?? Expression.Constant(this);
    }
    public Type ElementType => typeof(T);
    public Expression Expression { get; }
    public IQueryProvider Provider { get; }
    public IEnumerator<T> GetEnumerator() => Provider.Execute<IEnumerable<T>>(Expression).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
