using System;
using System.Linq;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Abstractions.Queries;

// These templates contain no query instances or captured values.
internal static class QueryTerminals<T>
{
    internal static readonly Expression<Func<IQueryable<T>, int>> Count = query => query.Count();
    internal static readonly Expression<Func<IQueryable<T>, long>> LongCount = query => query.LongCount();
    internal static readonly Expression<Func<IQueryable<T>, bool>> Any = query => query.Any();
    internal static readonly Expression<Func<IQueryable<T>, bool>> All = query => query.All(item => true);
    internal static readonly Expression<Func<IQueryable<T>, T>> First = query => query.First();
    internal static readonly Expression<Func<IQueryable<T>, T?>> FirstOrDefault = query => query.FirstOrDefault();
    internal static readonly Expression<Func<IQueryable<T>, T>> Single = query => query.Single();
    internal static readonly Expression<Func<IQueryable<T>, T?>> SingleOrDefault = query => query.SingleOrDefault();
}
