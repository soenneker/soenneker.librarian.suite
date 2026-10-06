using System;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Associates an original query expression with its generated executable delegate.</summary>
public static class GeneratedQueryFunctions
{
    private static readonly ConditionalWeakTable<LambdaExpression, Func<object?, int, object?>> Functions = new();

    /// <summary>Registers a compiled delegate without compiling or interpreting the expression.</summary>
    public static void Register<T, TResult>(Expression<Func<T, TResult>> expression, Func<T, TResult> function) =>
        Functions.GetValue(expression, _ => (value, _) => function((T)value!));

    /// <summary>Finds a generated delegate for the original expression instance.</summary>
    public static bool TryGet(LambdaExpression expression, out Func<object?, int, object?> function) =>
        Functions.TryGetValue(expression, out function!);
}
