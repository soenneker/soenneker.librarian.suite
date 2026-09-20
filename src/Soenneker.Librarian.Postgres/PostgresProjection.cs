using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Postgres;

internal sealed class PostgresProjection
{
    internal readonly List<(string Path, Type Type)> Columns = [];
    internal readonly List<PostgresScalar> Expressions = [];
    internal bool Scalar { get; private set; }
    internal Type ResultType { get; private set; } = null!;
    internal Func<string?[], object?> Materialize { get; private set; } = null!;
    private PostgresLambda _selector = null!;

    internal static PostgresProjection Create(PostgresLambda selector)
    {
        var projection = new PostgresProjection { ResultType = selector.ReturnType, _selector = selector };
        var row = Expression.Parameter(typeof(string[]), "row");
        Expression Column(Expression value)
        {
            var scalar = PostgresScalar.Create(value, selector.Parameters[0]);
            string path = scalar.Path ?? "";
            if (scalar.Path is not null) PostgresIndexValue.ValidatePath(path);
            Type type = Nullable.GetUnderlyingType(value.Type) ?? value.Type;
            if (!type.IsEnum && type != typeof(string) && type != typeof(bool) && type != typeof(decimal) &&
                type != typeof(int) && type != typeof(long) && type != typeof(short) && type != typeof(byte) &&
                type != typeof(uint) && type != typeof(ulong) && type != typeof(ushort) && type != typeof(sbyte) &&
                type != typeof(float) && type != typeof(double) && type != typeof(Guid) && type != typeof(DateTime) &&
                type != typeof(DateTimeOffset) && type != typeof(DateOnly) && type != typeof(TimeOnly))
                throw PostgresQueryPlan.Unsupported();
            int index = projection.Columns.Count;
            projection.Columns.Add((path, value.Type));
            projection.Expressions.Add(scalar);
            Expression column = Expression.ArrayIndex(row, Expression.Constant(index));
            Expression<Func<string, Type, object?>> read = (json, resultType) => Read(json, resultType);
            var call = (MethodCallExpression)read.Body;
            return Expression.Condition(Expression.Equal(column, Expression.Constant(null, typeof(string))),
                Expression.Default(value.Type),
                Expression.Convert(call.Update(null, [column, Expression.Constant(value.Type)]), value.Type));
        }

        Expression body;
        if (selector.Body is NewExpression constructor)
            body = constructor.Update(constructor.Arguments.Select(Column));
        else if (selector.Body is MemberInitExpression initializer && initializer.NewExpression.Arguments.Count == 0)
        {
            var bindings = new List<MemberBinding>();
            foreach (MemberBinding binding in initializer.Bindings)
            {
                if (binding is not MemberAssignment assignment) throw PostgresQueryPlan.Unsupported();
                bindings.Add(Expression.Bind(assignment.Member, Column(assignment.Expression)));
            }
            body = initializer.Update(initializer.NewExpression, bindings);
        }
        else
        {
            body = Column(selector.Body);
            projection.Scalar = true;
        }
        if (projection.Columns.Count == 0) throw PostgresQueryPlan.Unsupported();
        // Construction is materialization only: each leaf is fetched as a SQL column, never a full document.
        projection.Materialize = Expression.Lambda<Func<string?[], object?>>(Expression.Convert(body, typeof(object)), row).Compile(preferInterpretation: true);
        return projection;
    }

    private static object? Read(string json, Type type) =>
        LibrarianJson.Deserialize(json, type);

    internal PostgresLambda Rewrite(PostgresLambda expression)
    {
        Expression body = new RewriteProjection(expression.Parameters[0], _selector.Body).Visit(expression.Body)!;
        return new PostgresLambda(body, _selector.Parameters);
    }

    private sealed class RewriteProjection(ParameterExpression parameter, Expression selected) : ExpressionVisitor
    {
        protected override Expression VisitMember(MemberExpression node)
        {
            if (node.Expression == parameter)
            {
                if (selected is NewExpression { Members: not null } constructor)
                {
                    int index = constructor.Members.IndexOf(node.Member);
                    if (index >= 0) return constructor.Arguments[index];
                }
                if (selected is MemberInitExpression initializer)
                    foreach (MemberBinding binding in initializer.Bindings)
                        if (binding.Member == node.Member && binding is MemberAssignment assignment)
                        {
                            if (binding.Member is PropertyInfo property &&
                                (property.GetMethod is not { IsVirtual: false } getter || property.SetMethod is not { IsVirtual: false } setter ||
                                 !getter.IsDefined(typeof(CompilerGeneratedAttribute), false) || !setter.IsDefined(typeof(CompilerGeneratedAttribute), false)))
                                throw PostgresQueryPlan.Unsupported();
                            return assignment.Expression;
                        }
                if (selected is NewExpression or MemberInitExpression) throw PostgresQueryPlan.Unsupported();
            }
            return base.VisitMember(node);
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            if (node != parameter) return node;
            if (selected is NewExpression or MemberInitExpression) throw PostgresQueryPlan.Unsupported();
            return selected;
        }
    }
}
