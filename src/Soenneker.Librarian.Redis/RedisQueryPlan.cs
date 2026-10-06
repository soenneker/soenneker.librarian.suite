using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Redis;

public sealed class RedisQueryPlan
{
    internal readonly List<string> Paths = [];
    internal string? Terminal;
    public RedisProjection? Projection;
    private RedisQueryFilter? _filter;
    private bool _paged;

    internal RedisQueryFilter Filter => _filter ?? new RedisQueryFilter("all");
    internal string? Order { get; private set; }

    internal bool Descending { get; private set; }

    internal int Skip { get; private set; }

    internal int Take { get; private set; } = int.MaxValue;

    internal bool CountOnly => Terminal is nameof(Queryable.Count) or nameof(Queryable.LongCount) or nameof(Queryable.Any) or nameof(Queryable.All);

    public static RedisQueryPlan Create(Expression expression, IQueryProvider owner)
    {
        var plan = new RedisQueryPlan();
        plan.Parse(expression, owner);
        if (plan.Projection is not null && !plan.CountOnly && plan.Take == int.MaxValue) throw new NotSupportedException("Redis projections require a bounded page (Take, First, or Single).");
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
                if (_paged || Projection is not null) throw Unsupported();
                AddPredicate(Lambda(call.Arguments[1]));
                break;
            case nameof(Queryable.OrderBy):
            case nameof(Queryable.OrderByDescending):
                ApplyOrder(call);
                break;
            case nameof(Queryable.Select):
                if (Projection is not null || call.Arguments.Count != 2) throw Unsupported();
                Projection = RedisProjection.Create(Lambda(call.Arguments[1]));
                break;
            case nameof(Queryable.Skip):
                int skip = Math.Max(0, (int)Value(call.Arguments[1])!);
                int applied = Math.Min(skip, Take);
                Skip = checked(Skip + applied);
                Take -= applied;
                _paged = true;
                break;
            case nameof(Queryable.Take):
                Take = Math.Min(Take, Math.Max(0, (int)Value(call.Arguments[1])!));
                _paged = true;
                break;
            case nameof(Queryable.Count):
            case nameof(Queryable.LongCount):
            case nameof(Queryable.Any):
            case nameof(Queryable.All):
            case nameof(Queryable.First):
            case nameof(Queryable.FirstOrDefault):
            case nameof(Queryable.Single):
            case nameof(Queryable.SingleOrDefault):
                ApplyTerminal(call);
                break;
            default: throw Unsupported();
        }
    }

    private void ApplyOrder(MethodCallExpression call)
    {
        string method = call.Method.Name;
        if (_paged || Projection is not null) throw Unsupported();
        if (call.Arguments.Count != 2) throw Unsupported();
        LambdaExpression order = Lambda(call.Arguments[1]);
        Order = Register(Path(order.Body, order.Parameters[0]) ?? throw Unsupported());
        Descending = method == nameof(Queryable.OrderByDescending);
    }

    private void ApplyTerminal(MethodCallExpression call)
    {
        string method = call.Method.Name;
        if (call.Arguments.Count == 2)
        {
            if (_paged || Projection is not null) throw Unsupported();
            LambdaExpression predicate = Lambda(call.Arguments[1]);
            AddPredicate(predicate, negate: method == nameof(Queryable.All));
        }
        if (call.Arguments.Count > 2) throw Unsupported();
        Terminal = method;
        if (method is nameof(Queryable.Any) or nameof(Queryable.All) or nameof(Queryable.First) or nameof(Queryable.FirstOrDefault)) Take = Math.Min(Take, 1);
        if (method is nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault)) Take = Math.Min(Take, 2);
    }

    private static LambdaExpression Lambda(Expression expression) => expression is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda }
        && lambda.Parameters.Count == 1 ? lambda : throw Unsupported();

    private void AddPredicate(LambdaExpression predicate, bool negate = false)
    {
        RedisQueryFilter next = Predicate(predicate.Body, predicate.Parameters[0]);
        if (negate) next = new RedisQueryFilter("not", Left: next);
        _filter = _filter is null ? next : new RedisQueryFilter("and", Left: _filter, Right: next);
    }

    private RedisQueryFilter Predicate(Expression expression, ParameterExpression parameter)
    {
        if (expression is ConstantExpression { Value: bool booleanConstant }) return new RedisQueryFilter(booleanConstant ? "all" : "none");
        if (expression is MethodCallExpression call) return MethodPredicate(call, parameter);
        if (expression is BinaryExpression binary)
        {
            if (binary.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
                return new RedisQueryFilter(binary.NodeType == ExpressionType.AndAlso ? "and" : "or", Left: Predicate(binary.Left, parameter), Right: Predicate(binary.Right, parameter));
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
            return new RedisQueryFilter("not", Left: Predicate(unary.Operand, parameter));
        if (expression.Type == typeof(bool) && Path(expression, parameter) is { } boolean)
            return Term(boolean, ExpressionType.Equal, true);
        throw Unsupported();
    }

    private RedisQueryFilter MethodPredicate(MethodCallExpression call, ParameterExpression parameter)
    {
        if (call.Method.DeclaringType == typeof(string) && call.Method.Name == nameof(string.StartsWith) && call.Object is not null)
        {
            string path = Path(call.Object, parameter) ?? throw Unsupported();
            if (call.Arguments.Count is < 1 or > 2 || call.Arguments[0].Type != typeof(string)) throw Unsupported();
            if (call.Arguments.Count == 2 && (call.Arguments[1].Type != typeof(StringComparison) || !Equals(Value(call.Arguments[1]), StringComparison.Ordinal))) throw Unsupported();
            string value = Value(call.Arguments[0]) as string ?? throw new ArgumentNullException("value");
            string prefix = "3" + RedisIndexValue.Hex(value);
            return new RedisQueryFilter("term", Register(path), "[" + prefix + "!", "(" + prefix + "G!");
        }
        Expression? collection = null;
        Expression? item = null;
        if ((call.Method.DeclaringType == typeof(Enumerable) || call.Method.DeclaringType == typeof(MemoryExtensions)) &&
            call.Method.Name == nameof(Enumerable.Contains) && call.Arguments.Count == 2)
            (collection, item) = (call.Arguments[0], call.Arguments[1]);
        else if (call.Method.Name == nameof(IList.Contains) && call.Object is not null && call.Arguments.Count == 1)
            (collection, item) = (call.Object, call.Arguments[0]);
        if (collection is not null)
        {
            string path = Path(item!, parameter) ?? throw Unsupported();
            object? source = QueryExpressionReader.CollectionValue(collection, Unsupported);
            if (source is not IEnumerable values || !QueryTypes.IsMembershipCollection(source)) throw Unsupported();
            // Equality buckets are combined on the server; only the final page is fetched.
            return new RedisQueryFilter("in", Register(path), Values: values.Cast<object?>().Select(RedisIndexValue.Encode).Distinct(StringComparer.Ordinal).ToArray());
        }
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
            case ExpressionType.NotEqual: return new RedisQueryFilter("not", Left: Term(path, ExpressionType.Equal, value));
            case ExpressionType.GreaterThan: min = "(" + encoded + "!~"; max = "+"; break;
            case ExpressionType.GreaterThanOrEqual: max = "+"; break;
            case ExpressionType.LessThan: min = "-"; max = "(" + encoded + "!"; break;
            case ExpressionType.LessThanOrEqual: min = "-"; break;
            default: throw Unsupported();
        }
        return new RedisQueryFilter("term", Register(path), min, max);
    }

    private string Register(string path)
    {
        RedisIndexValue.ValidatePath(path);
        if (!Paths.Contains(path)) Paths.Add(path);
        return path;
    }

    internal static string? Path(Expression expression, ParameterExpression parameter) => QueryExpressionReader.Path(expression, parameter, Unsupported);

    private static object? Value(Expression expression) => QueryExpressionReader.Value(expression, Unsupported);

    internal static NotSupportedException Unsupported() => new("This LINQ expression cannot execute in Redis. Supported operations are scalar property comparisons, boolean AND/OR/NOT, ordinal StartsWith, membership, one ordering, Skip/Take, bounded projections, Count/LongCount/Any/All and First/Single. Filtering and ordering must precede paging; unsupported expressions never fall back to local evaluation.");
}
