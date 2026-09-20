using System;
using System.Linq.Expressions;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Core.Indexes;

internal sealed class QueryExpression : ExpressionVisitor
{
    internal static Expression Prepare(Expression expression) => new QueryExpression().Visit(expression)!;

    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        // C# 14 binds array.Contains to MemoryExtensions; the expression interpreter cannot box spans.
        if (node.Method.DeclaringType == typeof(MemoryExtensions) && node.Method.Name == nameof(MemoryExtensions.Contains) && node.Arguments.Count == 2 &&
            node.Arguments[0] is MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } conversion && conversion.Arguments[0].Type.IsArray)
        {
            Expression<Func<Array, object?, Type, bool>> template = (array, value, type) => Contains(array, value, type);
            return ((MethodCallExpression)template.Body).Update(null,
                [Expression.Convert(Visit(conversion.Arguments[0]), typeof(Array)), Expression.Convert(Visit(node.Arguments[1]), typeof(object)),
                 Expression.Constant(node.Method.GetGenericArguments()[0])]);
        }
        return base.VisitMethodCall(node);
    }

    private static bool Contains(Array array, object? value, Type type)
    {
        ArgumentNullException.ThrowIfNull(array);
        Func<object?, object?, bool> equal = QueryTypes.Get(type).Equal;
        foreach (object? item in array) if (equal(item, value)) return true;
        return false;
    }
}
