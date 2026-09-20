using System;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Soenneker.Librarian.Core.Indexes;

internal static class QueryFunction<TInput, TOutput>
{
    private static readonly ConditionalWeakTable<Expression<Func<TInput, TOutput>>, Func<TInput, TOutput>> _expressions = new();

    internal static Func<TInput, TOutput> Get(Expression<Func<TInput, TOutput>> expression)
    {
        // Reused expressions retain their live closures; fresh expressions avoid emitting a dynamic method.
        return _expressions.GetValue(expression, static expression => Expression.Lambda<Func<TInput, TOutput>>(QueryExpression.Prepare(expression.Body), expression.Parameters).Compile(preferInterpretation: true));
    }
}
