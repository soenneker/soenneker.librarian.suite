using System;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Postgres;

internal sealed record PostgresScalar(string Operation, Type Type, string? Path = null, object? Value = null,
    PostgresScalar? Left = null, PostgresScalar? Right = null)
{
    internal static bool Numeric(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(int) || type == typeof(long) || type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    }

    internal static PostgresScalar Create(Expression expression, ParameterExpression parameter)
    {
        if (expression is MemberExpression { Member.Name: nameof(string.Length), Expression.Type: var receiver } length && receiver == typeof(string))
            return new("length", typeof(int), Left: Create(length.Expression!, parameter));
        if (PostgresQueryPlan.Path(expression, parameter) is { } path)
            return new("path", expression.Type, path);
        if (expression is BinaryExpression binary && Numeric(expression.Type))
        {
            string operation = binary.NodeType switch
            {
                ExpressionType.Add or ExpressionType.AddChecked => "+",
                ExpressionType.Subtract or ExpressionType.SubtractChecked => "-",
                ExpressionType.Multiply or ExpressionType.MultiplyChecked => "*",
                ExpressionType.Divide => "/",
                ExpressionType.Modulo => "%",
                ExpressionType.Coalesce => "coalesce",
                _ => throw PostgresQueryPlan.Unsupported()
            };
            return new(operation, expression.Type, Left: Create(binary.Left, parameter), Right: Create(binary.Right, parameter));
        }
        if (expression is UnaryExpression unary && Numeric(expression.Type))
        {
            if (unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked && Numeric(unary.Operand.Type))
                return new("convert", expression.Type, Left: Create(unary.Operand, parameter));
            if (unary.NodeType is ExpressionType.Negate or ExpressionType.NegateChecked)
                return new("negate", expression.Type, Left: Create(unary.Operand, parameter));
        }
        return new("constant", expression.Type, Value: PostgresQueryPlan.Value(expression));
    }

    internal string Sql(Func<object, string> parameter, string alias)
    {
        string Cast(string sql)
        {
            Type type = Nullable.GetUnderlyingType(Type) ?? Type;
            string target = type == typeof(int) ? "integer" : type == typeof(long) ? "bigint" :
                type == typeof(float) ? "real" : type == typeof(double) ? "double precision" : type == typeof(decimal) ? "numeric" :
                type == typeof(bool) ? "boolean" : "text COLLATE \"C\"";
            return "(" + sql + ")::" + target;
        }
        if (Operation == "path") return Cast(alias + ".body #>> " + parameter(Path!.Split('.')));
        if (Operation == "constant") return Value is null ? "NULL" : Cast(parameter(Value));
        string left = Left!.Sql(parameter, alias);
        if (Operation == "length")
            return "(length(" + left + ") + regexp_count(" + left + @", U&'[\+010000-\+10FFFF]'))";
        if (Operation == "convert") return Cast(left);
        if (Operation == "negate") return Cast("-" + left);
        string right = Right!.Sql(parameter, alias);
        if (Operation == "coalesce") return Cast("COALESCE(" + left + "," + right + ")");
        if (Operation == "/" && (Nullable.GetUnderlyingType(Type) ?? Type) == typeof(decimal))
            return Cast("(" + left + ")::numeric(57,28) / (" + right + ")::numeric(57,28)");
        return Cast(left + " " + Operation + " " + right);
    }
}
