using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>Shared expression reading for providers that translate queries over JSON documents.</summary>
public static class QueryExpressionReader
{
    /// <summary>Reads a JSON member path rooted at the query parameter, rejecting scalar member access.</summary>
    public static string? Path(Expression expression, ParameterExpression parameter, Func<NotSupportedException> unsupported)
    {
        var segments = new Stack<string>();
        while (expression is MemberExpression member)
        {
            if (member.Member is not PropertyInfo && member.Member is not FieldInfo) return null;
            Type? parent = member.Expression?.Type;
            if (parent is not null && (parent.IsPrimitive || parent.IsEnum || parent == typeof(string) || parent == typeof(decimal) ||
                parent == typeof(DateTime) || parent == typeof(DateTimeOffset) || parent == typeof(Guid) ||
                parent == typeof(DateOnly) || parent == typeof(TimeOnly) || parent == typeof(TimeSpan) || Nullable.GetUnderlyingType(parent) is not null))
                return null;
            string segment = member.Member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                (JsonNamingPolicy.CamelCase.ConvertName(member.Member.Name) ?? member.Member.Name);
            if (segment.Contains('.', StringComparison.Ordinal)) throw unsupported();
            segments.Push(segment);
            expression = member.Expression!;
        }
        return expression == parameter && segments.Count > 0 ? string.Join('.', segments) : null;
    }

    /// <summary>Evaluates constants and captured values using the provider's unsupported-expression error.</summary>
    public static object? Value(Expression expression, Func<NotSupportedException> unsupported) => expression switch
    {
        ConstantExpression constant => constant.Value,
        MemberExpression { Member: FieldInfo field } member => field.GetValue(member.Expression is null ? null : Value(member.Expression, unsupported)),
        MemberExpression { Member: PropertyInfo property } member => property.GetValue(member.Expression is null ? null : Value(member.Expression, unsupported)),
        UnaryExpression { NodeType: ExpressionType.Convert } unary when unary.Operand is ConstantExpression => Expression.Lambda<Func<object?>>(Expression.Convert(unary, typeof(object))).Compile(preferInterpretation: true)(),
        NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array => ArrayValues(array, unsupported),
        _ => throw unsupported()
    };

    private static object?[] ArrayValues(NewArrayExpression array, Func<NotSupportedException> unsupported)
    {
        var values = new object?[array.Expressions.Count];
        for (var i = 0; i < values.Length; i++) values[i] = Value(array.Expressions[i], unsupported);
        return values;
    }

    /// <summary>Unwraps array-to-span and assignable conversions before evaluating a membership collection.</summary>
    public static object? CollectionValue(Expression expression, Func<NotSupportedException> unsupported)
    {
        // C# 14 binds array.Contains to MemoryExtensions and inserts an array-to-span conversion.
        // Inspect the original array without compiling or boxing the ref-struct value.
        if (expression is MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } conversion &&
            conversion.Type.IsGenericType && conversion.Type.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>) &&
            conversion.Arguments[0].Type.IsArray)
            expression = conversion.Arguments[0];
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert, Method: null } cast && cast.Type.IsAssignableFrom(cast.Operand.Type))
            expression = cast.Operand;
        return Value(expression, unsupported);
    }
}
