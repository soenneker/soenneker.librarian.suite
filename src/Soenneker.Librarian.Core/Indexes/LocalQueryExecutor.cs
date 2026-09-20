using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Core.Indexes;

// Explicit Enumerable dispatch avoids EnumerableQuery's runtime generic rebinding.
internal sealed class LocalQueryExecutor(Func<Expression, IEnumerable<object?>?> source)
{
    internal object? Execute(Expression expression)
    {
        object? result = Evaluate(expression);
        if (result is IEnumerable<object?> sequence && expression.Type.IsGenericType &&
            expression.Type.GetGenericTypeDefinition() is var definition &&
            (definition == typeof(IQueryable<>) || definition == typeof(IOrderedQueryable<>)))
            return QueryTypes.Get(expression.Type.GetGenericArguments()[0]).CastSequence(sequence);
        if (expression is ConstantExpression { Value: IQueryable query } && result is IEnumerable<object?> root)
            return QueryTypes.Get(query.ElementType).CastSequence(root);
        return result;
    }

    private object? Evaluate(Expression expression)
    {
        if (source(expression) is { } values) return values;
        if (expression is ConstantExpression constant)
            return constant.Value is IEnumerable items && constant.Value is not string ? items.Cast<object?>() : constant.Value;
        if (expression is not MethodCallExpression call || call.Method.DeclaringType != typeof(Queryable))
            throw new NotSupportedException("The local query operator is not supported by the AOT query executor.");
        IEnumerable<object?> input = (IEnumerable<object?>)Evaluate(call.Arguments[0])!;
        string name = call.Method.Name;
        LambdaExpression? lambda = call.Arguments.Count > 1 && call.Arguments[1] is UnaryExpression { Operand: LambdaExpression quoted } ? quoted : null;
        Func<object?, int, object?>? function = lambda is null ? null : Function(lambda);
        Func<object?, bool> predicate = value => (bool)function!(value, 0)!;
        switch (name)
        {
            case nameof(Queryable.Where): return input.Where((value, index) => (bool)function!(value, index)!);
            case nameof(Queryable.Select): return input.Select((value, index) => function!(value, index));
            case nameof(Queryable.Skip): return input.Skip((int)Value(call.Arguments[1])!);
            case nameof(Queryable.Take): return input.Take((int)Value(call.Arguments[1])!);
            case nameof(Queryable.Reverse): return input.Reverse();
            case nameof(Queryable.SequenceEqual):
                if (call.Arguments.Count != 2) throw Unsupported(name);
                return input.SequenceEqual((IEnumerable<object?>)Evaluate(call.Arguments[1])!, new Equality(QueryTypes.Get(call.Method.GetGenericArguments()[0])));
            case nameof(Queryable.Concat): return input.Concat((IEnumerable<object?>)Evaluate(call.Arguments[1])!);
            case nameof(Queryable.Cast):
                return input.Select(QueryTypes.Get(call.Method.GetGenericArguments()[0]).Cast);
            case nameof(Queryable.OfType): return input.Where(value => value is not null && call.Method.GetGenericArguments()[0].IsInstanceOfType(value));
            case nameof(Queryable.Distinct):
                if (call.Arguments.Count != 1) throw Unsupported(name);
                return input.Distinct(new Equality(QueryTypes.Get(call.Method.GetGenericArguments()[0])));
            case nameof(Queryable.OrderBy):
            case nameof(Queryable.OrderByDescending):
            case nameof(Queryable.ThenBy):
            case nameof(Queryable.ThenByDescending):
                if (call.Arguments.Count != 2) throw Unsupported(name);
                var comparer = Comparer<object?>.Create(QueryTypes.Get(lambda!.ReturnType).Compare);
                Func<object?, object?> key = value => function!(value, 0);
                return name switch
                {
                    nameof(Queryable.OrderBy) => input.OrderBy(key, comparer),
                    nameof(Queryable.OrderByDescending) => input.OrderByDescending(key, comparer),
                    nameof(Queryable.ThenBy) => ((IOrderedEnumerable<object?>)input).ThenBy(key, comparer),
                    _ => ((IOrderedEnumerable<object?>)input).ThenByDescending(key, comparer)
                };
            case nameof(Queryable.Count): return function is null ? input.Count() : input.Count(predicate);
            case nameof(Queryable.LongCount): return function is null ? input.LongCount() : input.LongCount(predicate);
            case nameof(Queryable.Any): return function is null ? input.Any() : input.Any(predicate);
            case nameof(Queryable.All): return input.All(predicate);
            case nameof(Queryable.Contains):
                if (call.Arguments.Count != 2) throw Unsupported(name);
                object? match = Value(call.Arguments[1]);
                return input.Any(value => QueryTypes.Get(call.Method.GetGenericArguments()[0]).Equal(value, match));
            case nameof(Queryable.First):
            case nameof(Queryable.Single):
            case nameof(Queryable.Last):
            case nameof(Queryable.FirstOrDefault):
            case nameof(Queryable.SingleOrDefault):
            case nameof(Queryable.LastOrDefault):
                if (function is not null) input = input.Where(predicate);
                object? fallback = call.Arguments.Count > (function is null ? 1 : 2)
                    ? Value(call.Arguments[^1]) : QueryTypes.Get(call.Type).Default;
                return name switch
                {
                    nameof(Queryable.First) => input.First(),
                    nameof(Queryable.Single) => input.Single(),
                    nameof(Queryable.Last) => input.Last(),
                    nameof(Queryable.FirstOrDefault) => input.FirstOrDefault(fallback),
                    nameof(Queryable.SingleOrDefault) => input.SingleOrDefault(fallback),
                    _ => input.LastOrDefault(fallback)
                };
            case nameof(Queryable.Sum):
            case nameof(Queryable.Average):
            case nameof(Queryable.Min):
            case nameof(Queryable.Max):
                if (call.Arguments.Count > 1 && function is null) throw Unsupported(name);
                if (function is not null) input = input.Select(value => function(value, 0));
                return Aggregate(input, name, lambda?.ReturnType ?? call.Arguments[0].Type.GetGenericArguments()[0]);
            default: throw Unsupported(name);
        }
    }

    private static object? Aggregate(IEnumerable<object?> input, string name, Type valueType)
    {
        Type type = Nullable.GetUnderlyingType(valueType) ?? valueType;
        bool nullable = Nullable.GetUnderlyingType(valueType) is not null;
        if (name is nameof(Queryable.Min) or nameof(Queryable.Max))
        {
            object? result = null;
            Comparison<object?> compare = QueryTypes.Get(valueType).Compare;
            foreach (object? value in input)
                if (value is not null && (result is null || (name == nameof(Queryable.Min) ? compare(value, result) < 0 : compare(value, result) > 0))) result = value;
            return result ?? (nullable || !valueType.IsValueType ? null : throw new InvalidOperationException("Sequence contains no elements"));
        }
        // Dispatch numeric overloads statically so overflow, floating-point and nullable behavior follow Enumerable.
        if (type == typeof(int))
        {
            var values = input.Select(value => (int?)value);
            if (name == nameof(Queryable.Sum)) return values.Sum();
            double? average = values.Average();
            return average ?? (nullable ? null : throw new InvalidOperationException("Sequence contains no elements"));
        }
        if (type == typeof(long))
        {
            var values = input.Select(value => (long?)value);
            if (name == nameof(Queryable.Sum)) return values.Sum();
            double? average = values.Average();
            return average ?? (nullable ? null : throw new InvalidOperationException("Sequence contains no elements"));
        }
        if (type == typeof(float))
        {
            var values = input.Select(value => (float?)value);
            if (name == nameof(Queryable.Sum)) return values.Sum();
            float? average = values.Average();
            return average ?? (nullable ? null : throw new InvalidOperationException("Sequence contains no elements"));
        }
        if (type == typeof(double))
        {
            var values = input.Select(value => (double?)value);
            if (name == nameof(Queryable.Sum)) return values.Sum();
            double? average = values.Average();
            return average ?? (nullable ? null : throw new InvalidOperationException("Sequence contains no elements"));
        }
        if (type == typeof(decimal))
        {
            var values = input.Select(value => (decimal?)value);
            if (name == nameof(Queryable.Sum)) return values.Sum();
            decimal? average = values.Average();
            return average ?? (nullable ? null : throw new InvalidOperationException("Sequence contains no elements"));
        }
        throw Unsupported(name);
    }

    private static Func<object?, int, object?> Function(LambdaExpression lambda)
    {
        if (lambda.Parameters.Count is < 1 or > 2) throw Unsupported("lambda");
        var value = Expression.Parameter(typeof(object));
        var index = Expression.Parameter(typeof(int));
        Expression body = new Replace(lambda.Parameters[0], Expression.Convert(value, lambda.Parameters[0].Type)).Visit(lambda.Body)!;
        if (lambda.Parameters.Count == 2) body = new Replace(lambda.Parameters[1], index).Visit(body)!;
        return Expression.Lambda<Func<object?, int, object?>>(Expression.Convert(QueryExpression.Prepare(body), typeof(object)), value, index).Compile(preferInterpretation: true);
    }

    private static object? Value(Expression expression) => Expression.Lambda<Func<object?>>(Expression.Convert(expression, typeof(object))).Compile(preferInterpretation: true)();
    private static NotSupportedException Unsupported(string name) => new($"Local query operator {name} is not supported. Use AsEnumerable() for additional client-side LINQ operations.");
    private sealed class Replace(Expression source, Expression replacement) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node) => node == source ? replacement : base.Visit(node);
    }
    private sealed class Equality(QueryType type) : IEqualityComparer<object?>
    {
        public new bool Equals(object? left, object? right) => type.Equal(left, right);
        public int GetHashCode(object? value) => value?.GetHashCode() ?? 0;
    }
}
