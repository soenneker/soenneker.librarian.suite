using System;
using System.Linq;
using System.Linq.Expressions;
namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Binds explicit typed values into LINQ expressions without compiler-generated closure fields.</summary>
public static class QueryParameters
{
    /// <summary>Filters using a typed parameter. The value is captured when this method is called.</summary>
    public static IQueryable<T> Where<T, TValue>(this IQueryable<T> source, TValue value, Expression<Func<T, TValue, bool>> predicate)
        => source.Where(Bind(value, predicate));

    /// <summary>Projects using a typed parameter. The value is captured when this method is called.</summary>
    public static IQueryable<TResult> Select<T, TValue, TResult>(this IQueryable<T> source, TValue value, Expression<Func<T, TValue, TResult>> selector)
        => source.Select(Bind(value, selector));

    /// <summary>Replaces the second lambda parameter with a typed constant usable by any LINQ provider.</summary>
    public static Expression<Func<T, TResult>> Bind<T, TValue, TResult>(TValue value, Expression<Func<T, TValue, TResult>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        Expression body = new QueryExpressionReplacement(expression.Parameters[1], Expression.Constant(value, typeof(TValue))).Visit(expression.Body)!;
        return Expression.Lambda<Func<T, TResult>>(body, expression.Parameters[0]);
    }
}
