using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Redis;

internal sealed class RedisProjection
{
    internal readonly List<(string Path, Type Type)> Columns = [];
    internal bool Scalar { get; private set; }
    internal Type ResultType { get; private set; } = null!;
    internal Func<string?[], object?> Materialize { get; private set; } = null!;

    internal static RedisProjection Create(LambdaExpression selector)
    {
        var projection = new RedisProjection { ResultType = selector.ReturnType };
        var row = Expression.Parameter(typeof(string[]), "row");
        Expression Column(Expression value)
        {
            string path = RedisQueryPlan.Path(value, selector.Parameters[0]) ?? throw RedisQueryPlan.Unsupported();
            RedisIndexValue.ValidatePath(path);
            Type type = Nullable.GetUnderlyingType(value.Type) ?? value.Type;
            if (!type.IsEnum && type != typeof(string) && type != typeof(bool) && type != typeof(decimal) &&
                type != typeof(int) && type != typeof(long) && type != typeof(short) && type != typeof(byte) &&
                type != typeof(uint) && type != typeof(ulong) && type != typeof(ushort) && type != typeof(sbyte) &&
                type != typeof(float) && type != typeof(double) && type != typeof(Guid) && type != typeof(DateTime) &&
                type != typeof(DateTimeOffset) && type != typeof(DateOnly) && type != typeof(TimeOnly))
                throw RedisQueryPlan.Unsupported();
            int index = projection.Columns.Count;
            projection.Columns.Add((path, value.Type));
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
                if (binding is not MemberAssignment assignment) throw RedisQueryPlan.Unsupported();
                bindings.Add(Expression.Bind(assignment.Member, Column(assignment.Expression)));
            }
            body = initializer.Update(initializer.NewExpression, bindings);
        }
        else
        {
            body = Column(selector.Body);
            projection.Scalar = true;
        }
        if (projection.Columns.Count == 0) throw RedisQueryPlan.Unsupported();
        // Construction is materialization only: each leaf is fetched as a SQL column, never a full document.
        projection.Materialize = Expression.Lambda<Func<string?[], object?>>(Expression.Convert(body, typeof(object)), row).Compile(preferInterpretation: true);
        return projection;
    }

    private static object? Read(string json, Type type) =>
        LibrarianJson.Deserialize(json, type);

    internal object? FromDocument(string document)
    {
        using JsonDocument json = JsonDocument.Parse(document);
        var fields = new string?[Columns.Count];
        for (var i = 0; i < fields.Length; i++)
        {
            JsonElement value = json.RootElement;
            var found = true;
            foreach (string segment in Columns[i].Path.Split('.'))
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) { found = false; break; }
            if (found) fields[i] = value.GetRawText();
        }
        return Materialize(fields);
    }
}
