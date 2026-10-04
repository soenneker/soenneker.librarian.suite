using System;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Reflection;

namespace Soenneker.Librarian.Core.Indexes;

internal static class QueryFunction<TInput, TOutput>
{
    private static readonly ConditionalWeakTable<Expression<Func<TInput, TOutput>>, Func<TInput, TOutput>> _expressions = new();

    private static readonly ConditionalWeakTable<PropertyInfo, Func<TInput, TOutput>> _getters = new();

    internal static Func<TInput, TOutput> Get(Expression<Func<TInput, TOutput>> expression)
    {
        if (!typeof(TInput).IsValueType && expression.Body is MemberExpression { Member: PropertyInfo property } member
            && member.Expression == expression.Parameters[0] && property.DeclaringType == typeof(TInput) && property.PropertyType == typeof(TOutput)
            && property.GetMethod is { IsStatic: false })
            return _getters.GetValue(property, static key => key.GetMethod!.CreateDelegate<Func<TInput, TOutput>>());
        // Reused expressions retain their live closures; fresh expressions avoid emitting a dynamic method.
        return _expressions.GetValue(expression, static expression => Expression.Lambda<Func<TInput, TOutput>>(QueryExpression.Prepare(expression.Body), expression.Parameters).Compile(preferInterpretation: true));
    }
}
