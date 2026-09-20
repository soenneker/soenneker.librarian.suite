using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Linq.Expressions;

namespace Soenneker.Librarian.Abstractions.Queries;

// Every factory is instantiated by a generic API call, never MakeGenericType/Activator.
internal static class QueryTypes
{
    private static readonly ConcurrentDictionary<Type, QueryType> _types = new();
    private static readonly ConcurrentDictionary<Type, QueryType> _queries = new();
    private static readonly ConcurrentDictionary<Type, Func<object, bool>> _sets = new();

    // C# emits unbound decimal operators in expression trees. They must survive trimming
    // before the provider receives the tree; other user-defined operators carry MethodInfo directly.
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(decimal))]
    static QueryTypes()
    {
        Register<string>();
        Register<bool>();
        Register<bool?>();
        Register<byte>();
        Register<byte?>();
        Register<sbyte>();
        Register<sbyte?>();
        Register<short>();
        Register<short?>();
        Register<ushort>();
        Register<ushort?>();
        Register<int>();
        Register<int?>();
        Register<uint>();
        Register<uint?>();
        Register<long>();
        Register<long?>();
        Register<ulong>();
        Register<ulong?>();
        Register<float>();
        Register<float?>();
        Register<double>();
        Register<double?>();
        Register<decimal>();
        Register<decimal?>();
        Register<Guid>();
        Register<Guid?>();
        Register<DateTime>();
        Register<DateTime?>();
        Register<DateTimeOffset>();
        Register<DateTimeOffset?>();
        Register<DateOnly>();
        Register<DateOnly?>();
        Register<TimeOnly>();
        Register<TimeOnly?>();
        Register<TimeSpan>();
        Register<TimeSpan?>();
        Register<char>();
        Register<char?>();
        Register<System.Text.Json.JsonElement>();
        Register<System.Text.Json.JsonElement?>();
    }

    internal static QueryType Register<T>() => Cache<T>.Value;

    private static class Cache<T>
    {
        internal static readonly QueryType Value = Add<T>();
    }

    private static QueryType Add<T>()
    {
        QueryType entry = _types.GetOrAdd(typeof(T), static _ => new QueryType(
            static () => new List<T>(), default(T),
            static (provider, expression) => provider.CreateQuery<T>(expression),
            static values => values.Select(static value => (T)value!),
            static (left, right) => Comparer<T>.Default.Compare((T)left!, (T)right!),
            static (left, right) => EqualityComparer<T>.Default.Equals((T)left!, (T)right!),
            static value => (T)value!));
        _queries.TryAdd(typeof(IQueryable<T>), entry);
        _queries.TryAdd(typeof(IOrderedQueryable<T>), entry);
        _sets.TryAdd(typeof(HashSet<T>), static source =>
        {
            IEqualityComparer<T> comparer = ((HashSet<T>)source).Comparer;
            return Equals(comparer, EqualityComparer<T>.Default) || ReferenceEquals(comparer, StringComparer.Ordinal);
        });
        return entry;
    }

    internal static QueryType Get(Type type) => _types.TryGetValue(type, out QueryType? entry) ? entry
        : throw new NotSupportedException($"Query type {type} has not been registered by a generic query or LibrarianJson.Register<T>().");

    internal static IQueryable CreateQuery(IQueryProvider provider, Expression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (_queries.TryGetValue(expression.Type, out QueryType? entry)) return entry.CreateQuery(provider, expression);
        if (expression is ConstantExpression { Value: IQueryable query } && query.Provider == provider) return query;
        throw new ArgumentException("Use generic CreateQuery<T> for a new query element type.", nameof(expression));
    }

    internal static bool IsMembershipCollection(object source)
    {
        Type type = source.GetType();
        if (type.IsArray) return true;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)) return true;
        return _sets.TryGetValue(type, out Func<object, bool>? accepted) && accepted(source);
    }
}

internal sealed record QueryType(Func<IList> CreateList, object? Default,
    Func<IQueryProvider, Expression, IQueryable> CreateQuery, Func<IEnumerable<object?>, object> CastSequence,
    Comparison<object?> Compare, Func<object?, object?, bool> Equal, Func<object?, object?> Cast);
