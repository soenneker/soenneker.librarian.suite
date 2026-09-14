using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Soenneker.Librarian.Core.Indexes;

internal static class QueryFunction<TInput, TOutput>
{
    private static readonly ConditionalWeakTable<Expression<Func<TInput, TOutput>>, Func<TInput, TOutput>> _expressions = new();
    private static readonly ConditionalWeakTable<PropertyInfo, Func<TInput, TOutput>> _getters = new();

    internal static Func<TInput, TOutput> Get(Expression<Func<TInput, TOutput>> expression)
    {
        if (!typeof(TInput).IsValueType && expression.Body is MemberExpression { Member: PropertyInfo property } member
            && member.Expression == expression.Parameters[0] && property.GetMethod is not null && property.PropertyType == typeof(TOutput))
            return _getters.GetValue(property, static property => property.GetMethod!.CreateDelegate<Func<TInput, TOutput>>());
        // Reused expressions retain their live closures; fresh expressions avoid emitting a dynamic method.
        return _expressions.GetValue(expression, static expression => expression.Compile(preferInterpretation: true));
    }
}
