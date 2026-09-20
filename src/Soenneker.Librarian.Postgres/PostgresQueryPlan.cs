using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Soenneker.Librarian.Abstractions.Queries;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soenneker.Librarian.Postgres;

internal sealed class PostgresQueryPlan
{
    internal readonly List<string> Paths = [];
    internal string? Terminal;
    private PostgresQueryFilter? _filter;
    internal readonly List<(string Path, bool Descending)> Orders = [];
    internal PostgresProjection? Projection;
    internal string? AggregatePath;
    internal Type? AggregateType;
    internal PostgresScalar? AggregateExpression;
    private long _skip;
    private long _take = long.MaxValue;
    private bool _paged;
    internal readonly List<PostgresQueryStage> Stages = [];
    internal PostgresQueryStage CurrentStage => new(Filter, Orders.ToArray(), Skip, Take);

    internal PostgresQueryFilter Filter => _filter ?? new("all");
    internal long Skip => _skip;
    internal long Take => _take;
    internal bool CountOnly => Terminal is nameof(Queryable.Count) or nameof(Queryable.LongCount) or nameof(Queryable.Any) or nameof(Queryable.All);
    internal bool Aggregate => AggregateExpression is not null;

    internal static PostgresQueryPlan Create(Expression expression, IQueryProvider owner)
    {
        var plan = new PostgresQueryPlan();
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
                BeginStageAfterPage();
                AddPredicate(Resolve(Lambda(call.Arguments[1])));
                break;
            case nameof(Queryable.OrderBy):
            case nameof(Queryable.OrderByDescending):
            case nameof(Queryable.ThenBy):
            case nameof(Queryable.ThenByDescending):
                if (call.Arguments.Count != 2) throw Unsupported();
                bool secondary = method is nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending);
                if (secondary && _paged) throw Unsupported();
                BeginStageAfterPage();
                if (secondary && Orders.Count == 0) throw Unsupported();
                if (!secondary) Orders.Clear();
                PostgresLambda order = Resolve(Lambda(call.Arguments[1]));
                Orders.Add((Register(Path(order.Body, order.Parameters[0]) ?? throw Unsupported()),
                    method is nameof(Queryable.OrderByDescending) or nameof(Queryable.ThenByDescending)));
                break;
            case nameof(Queryable.Select):
                if (call.Arguments.Count != 2) throw Unsupported();
                Projection = PostgresProjection.Create(Resolve(Lambda(call.Arguments[1])));
                break;
            case nameof(Queryable.Distinct):
                if (call.Arguments.Count != 1 || Projection?.Scalar != true) throw Unsupported();
                Type distinctType = Nullable.GetUnderlyingType(Projection.ResultType) ?? Projection.ResultType;
                if (!PostgresScalar.Numeric(distinctType) && distinctType != typeof(string) && distinctType != typeof(bool)) throw Unsupported();
                Stages.Add(CurrentStage with { Distinct = Projection.Expressions[0] });
                ResetStage();
                break;
            case nameof(Queryable.Skip):
                int skip = Math.Max(0, (int)Value(call.Arguments[1])!);
                long applied = Math.Min(skip, _take);
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
            case nameof(Queryable.All):
            case nameof(Queryable.First):
            case nameof(Queryable.FirstOrDefault):
            case nameof(Queryable.Single):
            case nameof(Queryable.SingleOrDefault):
                if (call.Arguments.Count == 2)
                {
                    BeginStageAfterPage();
                    PostgresLambda predicate = Resolve(Lambda(call.Arguments[1]));
                    PostgresQueryFilter next = Predicate(predicate.Body, predicate.Parameters[0]);
                    if (method == nameof(Queryable.All)) next = new("not", Left: next);
                    _filter = _filter is null ? next : new("and", Left: _filter, Right: next);
                }
                if (call.Arguments.Count > 2) throw Unsupported();
                Terminal = method;
                if (method is nameof(Queryable.Any) or nameof(Queryable.All) or nameof(Queryable.First) or nameof(Queryable.FirstOrDefault)) _take = Math.Min(_take, 1);
                if (method is nameof(Queryable.Single) or nameof(Queryable.SingleOrDefault)) _take = Math.Min(_take, 2);
                break;
            case nameof(Queryable.Sum):
            case nameof(Queryable.Average):
            case nameof(Queryable.Min):
            case nameof(Queryable.Max):
                if (call.Arguments.Count == 2)
                {
                    PostgresLambda selector = Resolve(Lambda(call.Arguments[1]));
                    AggregateExpression = PostgresScalar.Create(selector.Body, selector.Parameters[0]);
                    AggregatePath = AggregateExpression.Path;
                    AggregateType = selector.ReturnType;
                }
                else if (call.Arguments.Count == 1 && Projection?.Scalar == true)
                {
                    AggregatePath = Projection.Columns[0].Path;
                    AggregateExpression = Projection.Expressions[0];
                    AggregateType = Projection.Columns[0].Type;
                }
                else throw Unsupported();
                if (!string.IsNullOrEmpty(AggregatePath)) PostgresIndexValue.ValidatePath(AggregatePath);
                Type numeric = Nullable.GetUnderlyingType(AggregateType) ?? AggregateType;
                if (numeric != typeof(int) && numeric != typeof(long) && numeric != typeof(float) && numeric != typeof(double) && numeric != typeof(decimal))
                    throw Unsupported();
                Terminal = method;
                break;
            default: throw Unsupported();
        }
    }

    private static PostgresLambda Lambda(Expression expression) => expression is UnaryExpression { NodeType: ExpressionType.Quote, Operand: LambdaExpression lambda }
        && lambda.Parameters.Count == 1 ? new PostgresLambda(lambda.Body, [lambda.Parameters[0]]) : throw Unsupported();

    private PostgresLambda Resolve(PostgresLambda expression) => Projection?.Rewrite(expression) ?? expression;

    private void BeginStageAfterPage()
    {
        if (!_paged) return;
        Stages.Add(CurrentStage);
        ResetStage();
    }

    private void ResetStage()
    {
        _filter = null;
        Orders.Clear();
        _skip = 0;
        _take = long.MaxValue;
        _paged = false;
    }

    private void AddPredicate(PostgresLambda predicate)
    {
        PostgresQueryFilter next = Predicate(predicate.Body, predicate.Parameters[0]);
        _filter = _filter is null ? next : new("and", Left: _filter, Right: next);
    }

    private PostgresQueryFilter Predicate(Expression expression, ParameterExpression parameter)
    {
        if (expression is ConstantExpression { Value: bool booleanConstant }) return new(booleanConstant ? "all" : "none");
        if (expression is MethodCallExpression call)
        {
            if (call.Method.DeclaringType == typeof(string) && call.Object is not null &&
                call.Method.Name is nameof(string.StartsWith) or nameof(string.EndsWith) or nameof(string.Contains))
            {
                string path = Path(call.Object, parameter) ?? throw Unsupported();
                if (call.Arguments.Count is < 1 or > 2 || call.Arguments[0].Type != typeof(string)) throw Unsupported();
                if (call.Arguments.Count == 2 && (call.Arguments[1].Type != typeof(StringComparison) ||
                    !Equals(Value(call.Arguments[1]), StringComparison.Ordinal))) throw Unsupported();
                string value = Value(call.Arguments[0]) as string ?? throw new ArgumentNullException("value");
                string hex = PostgresIndexValue.Hex(value);
                return call.Method.Name switch
                {
                    nameof(string.StartsWith) => new("prefix", Register(path), Value: "3" + hex),
                    nameof(string.EndsWith) => new("pattern", Register(path), Value: "3%" + hex),
                    // Match only aligned UTF-16 code units, never an arbitrary substring of the hex encoding.
                    _ => new("regex", Register(path), Value: "^3([0-9A-F]{4})*" + hex)
                };
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
                object? source = CollectionValue(collection);
                if (source is not IEnumerable values || !IsMembershipCollection(source)) throw Unsupported();
                string[] encoded = values.Cast<object?>().Select(PostgresIndexValue.Encode).Distinct(StringComparer.Ordinal).ToArray();
                return new("in", Register(path), Values: encoded);
            }
            throw Unsupported();
        }
        if (expression is BinaryExpression binary)
        {
            if (binary.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse)
                return new(binary.NodeType == ExpressionType.AndAlso ? "and" : "or", Left: Predicate(binary.Left, parameter), Right: Predicate(binary.Right, parameter));
            if (binary.NodeType is ExpressionType.Equal or ExpressionType.NotEqual or ExpressionType.LessThan or ExpressionType.LessThanOrEqual or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual &&
                ((Path(binary.Left, parameter) is not null && Path(binary.Right, parameter) is not null) ||
                 (Path(binary.Left, parameter) is null && Path(binary.Right, parameter) is null) ||
                 (Path(binary.Left, parameter) is null && References(binary.Left, parameter)) ||
                 (Path(binary.Right, parameter) is null && References(binary.Right, parameter))))
            {
                var left = PostgresScalar.Create(binary.Left, parameter);
                var right = PostgresScalar.Create(binary.Right, parameter);
                if (!PostgresScalar.Numeric(left.Type) || !PostgresScalar.Numeric(right.Type)) throw Unsupported();
                string computedComparison = binary.NodeType switch
                {
                    ExpressionType.Equal => "IS NOT DISTINCT FROM", ExpressionType.NotEqual => "IS DISTINCT FROM",
                    ExpressionType.LessThan => "<", ExpressionType.LessThanOrEqual => "<=", ExpressionType.GreaterThan => ">", _ => ">="
                };
                return new("computed", Comparison: computedComparison, ScalarLeft: left, ScalarRight: right);
            }
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

    private PostgresQueryFilter Term(string path, ExpressionType comparison, object? value)
    {
        string encoded = PostgresIndexValue.Encode(value);
        string operation = comparison switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "!=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            _ => throw Unsupported()
        };
        if (operation == "!=") return new("not", Left: Term(path, ExpressionType.Equal, value));
        return new("term", Register(path), operation, encoded);
    }

    private string Register(string path)
    {
        PostgresIndexValue.ValidatePath(path);
        if (!Paths.Contains(path)) Paths.Add(path);
        return path;
    }

    internal static string? Path(Expression expression, ParameterExpression parameter)
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
            if (segment.Contains('.', StringComparison.Ordinal)) throw Unsupported();
            segments.Push(segment);
            expression = member.Expression!;
        }
        return expression == parameter && segments.Count > 0 ? string.Join('.', segments) : null;
    }

    internal static object? Value(Expression expression) => expression switch
    {
        ConstantExpression constant => constant.Value,
        MemberExpression { Member: FieldInfo field } member => field.GetValue(member.Expression is null ? null : Value(member.Expression)),
        MemberExpression { Member: PropertyInfo property } member => property.GetValue(member.Expression is null ? null : Value(member.Expression)),
        UnaryExpression { NodeType: ExpressionType.Convert } unary when unary.Operand is ConstantExpression => Expression.Lambda<Func<object?>>(Expression.Convert(unary, typeof(object))).Compile(preferInterpretation: true)(),
        NewArrayExpression { NodeType: ExpressionType.NewArrayInit } array => array.Expressions.Select(Value).ToArray(),
        _ => throw Unsupported()
    };

    private static bool IsMembershipCollection(object source)
    {
        return QueryTypes.IsMembershipCollection(source);
    }

    private static object? CollectionValue(Expression expression)
    {
        // C# 14 binds array.Contains to MemoryExtensions and inserts an array-to-span conversion.
        // Inspect the original array without compiling or boxing the ref-struct value.
        if (expression is MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } conversion &&
            conversion.Type.IsGenericType && conversion.Type.GetGenericTypeDefinition() == typeof(ReadOnlySpan<>) &&
            conversion.Arguments[0].Type.IsArray)
            expression = conversion.Arguments[0];
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert, Method: null } cast && cast.Type.IsAssignableFrom(cast.Operand.Type))
            expression = cast.Operand;
        return Value(expression);
    }

    private static bool References(Expression expression, ParameterExpression parameter)
    {
        var visitor = new ParameterReference(parameter);
        visitor.Visit(expression);
        return visitor.Found;
    }

    private sealed class ParameterReference(ParameterExpression parameter) : ExpressionVisitor
    {
        internal bool Found;
        protected override Expression VisitParameter(ParameterExpression node) { Found |= node == parameter; return node; }
    }

    internal static NotSupportedException Unsupported() => new("This LINQ expression cannot execute in PostgreSQL. See docs/POSTGRES.md for supported operators and overloads; unsupported expressions never fall back to local evaluation.");
}
