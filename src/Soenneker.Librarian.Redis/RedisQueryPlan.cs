using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Soenneker.Json.OptionsCollection;
using System.Text.Json.Serialization;

namespace Soenneker.Librarian.Redis;

internal sealed class RedisQueryPlan
{
    internal readonly List<string> Paths = [];
    internal string? Terminal;
    private RedisQueryFilter? _filter;
    private string? _order;
    private bool _descending;
    private int _skip;
    private int _take = int.MaxValue;
    private bool _paged;

    internal RedisQueryFilter Filter => _filter ?? new("all");
    internal string? Order => _order;
    internal bool Descending => _descending;
    internal int Skip => _skip;
    internal int Take => _take;
    internal bool CountOnly => Terminal is nameof(Queryable.Count) or nameof(Queryable.LongCount) or nameof(Queryable.Any);

    internal static RedisQueryPlan Create(Expression expression, IQueryProvider owner)
    {
        var plan = new RedisQueryPlan();
        plan.Parse(expression, owner);
        return plan;
    }

    private void Parse(Expression expression, IQueryProvider owner)
    {
        if (expression is ConstantExpression { Value: IQueryable root } && ReferenceEquals(root.Provider, owner)) return;
        if (expression is not MethodCallExpression call || call.Method.DeclaringType != typeof(Queryable)) throw Unsupported();
        Parse(call.Arguments[0], owner);
        if (Terminal is not null) throw Unsupported();
        string method = call.Method.Name;
        switch (method)
        {
            case nameof(Queryable.Where):
                if (_paged) throw Unsupported();
                AddPredicate(Lambda(call.Arguments[1]));
                break;
            case nameof(Queryable.OrderBy):
            case nameof(Queryable.OrderByDescending):
                if (_paged) throw Unsupported();
                LambdaExpression order = Lambda(call.Arguments[1]);
                _order = Register(Path(order.Body, order.Parameters[0]) ?? throw Unsupported());
                _descending = method == nameof(Queryable.OrderByDescending);
                break;
            case nameof(Queryable.Skip):
                int skip = Math.Max(0, (int)Value(call.Arguments[1])!);
                int applied = Math.Min(skip, _take);
                _skip = checked(_skip + applied);
                _take -= applied;
                _paged = true;
                break;
            case nameof(Queryable.Take):
                _take = Math.Min(_take, Math.Max(0, (int)Value(call.Arguments[1])!));
                _paged = true;
                break;
            case nameof(Queryable.Count):
            case nameof(Queryable.LongCount):
            case nameof(Queryable.Any):
            case nameof(Queryable.First):
            case nameof(Queryable.FirstOrDefault):
            case nameof(Queryable.Single):
            case nameof(Queryable.SingleOrDefault):
                if (call.Arguments.Count == 2)
                {
                    if (_paged) throw Unsupported();
                    AddPredicate(Lambda(call.Arguments[1]));
                }
                if (call.Arguments.Count > 2) throw Unsupported();
                Terminal = method;
                if (method is nameof(Queryable.Any) or nameof(Queryable.First) or nameof(Queryable.FirstOrDefault)) _take = Math.Min(_take, 1);
                if (method is nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault)) _take = Math.Min(_take, 2);
                break;
            default: throw Unsupported();
        }
    }

    private static LambdaExpression Lambda(Expression expression) => expression is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda }
        && lambda.Parameters.Count == 1 ? lambda : throw Unsupported();

    private void AddPredicate(LambdaExpression predicate)
    {
        RedisQueryFilter next = Predicate(predicate.Body, predicate.Parameters[0]);
        _filter = _filter is null ? next : new("and", Left: _filter, Right: next);
    }

    private RedisQueryFilter Predicate(Expression expression, ParameterExpression parameter)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
                return new(binary.NodeType == ExpressionType.AndAlso ? "and" : "or", Left: Predicate(binary.Left, parameter), Right: Predicate(binary.Right, parameter));
            string? path = Path(binary.Left, parameter);
            Expression constant = binary.Right;
            ExpressionType comparison = binary.NodeType;
            if (path is null)
            {
                path = Path(binary.Right, parameter) ?? throw Unsupported();
                constant = binary.Left;
                comparison = comparison switch
                {
                    ExpressionType.LessThan => ExpressionType.GreaterThan,
                    ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
                    ExpressionType.GreaterThan => ExpressionType.LessThan,
                    ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
                    _ => comparison
                };
            }
            return Term(path, comparison, Value(constant));
        }
        if (expression is UnaryExpression { NodeType: ExpressionType.Not } unary)
            return new("not", Left: Predicate(unary.Operand, parameter));
        if (expression.Type == typeof(bool) && Path(expression, parameter) is { } boolean)
            return Term(boolean, ExpressionType.Equal, true);
        throw Unsupported();
    }

    private RedisQueryFilter Term(string path, ExpressionType comparison, object? value)
    {
        string encoded = RedisIndexValue.Encode(value);
        string lower = "[" + encoded + "!", upper = "[" + encoded + "!~";
        string min = lower, max = upper;
        switch (comparison)
        {
            case ExpressionType.Equal: break;
            case ExpressionType.NotEqual: return new("not", Left: Term(path, ExpressionType.Equal, value));
            case ExpressionType.GreaterThan: min = "(" + encoded + "!~"; max = "+"; break;
            case ExpressionType.GreaterThanOrEqual: max = "+"; break;
            case ExpressionType.LessThan: min = "-"; max = "(" + encoded + "!"; break;
            case ExpressionType.LessThanOrEqual: min = "-"; break;
            default: throw Unsupported();
        }
        return new("term", Register(path), min, max);
    }

    private string Register(string path)
    {
        if (!Paths.Contains(path)) Paths.Add(path);
        return path;
    }

    private static string? Path(Expression expression, ParameterExpression parameter)
    {
        var segments = new Stack<string>();
        while (expression is MemberExpression member)
        {
            if (member.Member is not PropertyInfo && member.Member is not FieldInfo) return null;
            segments.Push(member.Member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? (JsonOptionsCollection.WebOptions.PropertyNamingPolicy?.ConvertName(member.Member.Name) ?? member.Member.Name));
            expression = member.Expression!;
        }
        return expression == parameter && segments.Count > 0 ? string.Join('.', segments) : null;
    }

    private static object? Value(Expression expression) => expression switch
    {
        ConstantExpression constant => constant.Value,
        MemberExpression { Member: FieldInfo field } member => field.GetValue(member.Expression is null ? null : Value(member.Expression)),
        MemberExpression { Member: PropertyInfo property } member => property.GetValue(member.Expression is null ? null : Value(member.Expression)),
        UnaryExpression { NodeType: ExpressionType.Convert } unary when unary.Operand is ConstantExpression => Expression.Lambda(unary).Compile().DynamicInvoke(),
        _ => throw Unsupported()
    };

    private static NotSupportedException Unsupported() => new("This LINQ expression cannot execute in Redis. Supported operations are scalar property comparisons, boolean AND/OR/NOT, one ordering, Skip/Take, Count/LongCount/Any and First/Single. Filtering and ordering must precede paging; unsupported expressions never fall back to local evaluation.");
}
