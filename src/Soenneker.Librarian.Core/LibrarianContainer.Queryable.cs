using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Core.Indexes;
using Soenneker.Utils.Json;

namespace Soenneker.Librarian.Core;

public sealed partial class LibrarianContainer
{
    private readonly Dictionary<(Type, PropertyInfo), AutomaticIndex> _automaticIndexes = new();
    private readonly Dictionary<Type, AutomaticIndexGroup> _automaticIndexGroups = new();
    private KeyValuePair<string, string>[]? _queryScanSnapshot;
    internal void CheckQueryLifetime() => ThrowIfDisposed();

    internal async ValueTask<IEnumerable<T>> QuerySource<T>(QueryPlan? plan)
    {
        if (plan is not null)
        {
            using IndexPage page = await QueryPage<T>(plan).NoSync();
            if (page.Count == 0) return Array.Empty<T>();
            // An owned raw snapshot preserves consistency and can be enumerated repeatedly without retaining a pool lease.
            var json = new string[page.Count];
            Array.Copy(page.Documents, json, page.Count);
            IEnumerable<T> values = ScanValues<T>(json);
            return plan.ResidualPredicate is Expression<Func<T, bool>> predicate
                ? values.Where(QueryFunction<T, bool>.Get(predicate)) : values;
        }
        KeyValuePair<string, string>[] snapshot;
        using (await _mutationGate.Lock().NoSync())
        {
            ThrowIfDisposed();
            snapshot = _queryScanSnapshot ??= _items.ToArray();
        }
        return Scan<T>(snapshot);
    }

    private static IEnumerable<T> ScanValues<T>(string[] json)
    {
        foreach (string document in json)
        {
            var value = JsonUtil.Deserialize<T>(document);
            if (value is not null) yield return value;
        }
    }

    private IEnumerable<T> Scan<T>(KeyValuePair<string, string>[] snapshot)
    {
        foreach ((string id, string json) in snapshot)
        {
            T? value;
            try { value = JsonUtil.Deserialize<T>(json); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to deserialize item ({id})", id);
                continue;
            }
            if (value is not null) yield return value;
        }
    }

    internal async ValueTask<IReadOnlyList<T>> QuerySnapshot<T>(QueryPlan plan)
    {
        using IndexPage page = await QueryPage<T>(plan).NoSync();
        return DeserializePage<T>(page, default);
    }

    private async ValueTask<IndexPage> QueryPage<T>(QueryPlan plan)
    {
        IndexPage page;
        using (await _mutationGate.Lock().NoSync())
        {
            ThrowIfDisposed();
            if (plan.Empty || plan.Take == 0) return IndexPage.Empty;
            if (plan.AdditionalFilters is not null || plan.OrderProperty is { } order && order != plan.Property)
            {
                page = QueryMultipleIndexes<T>(plan);
            }
            else
            {
                AutomaticIndex automatic = GetAutomaticIndex<T>(plan.Property);
                bool equality = plan.Minimum is { } && plan.Minimum == plan.Maximum && plan.IncludeMinimum && plan.IncludeMaximum;
                if (plan.CountOnly)
                {
                    int count = equality ? automatic.Index.Count(plan.Minimum!.Value)
                        : automatic.Index.CountRange(plan.Minimum, plan.Maximum, plan.IncludeMinimum, plan.IncludeMaximum);
                    plan.Count = Math.Min(plan.Take, Math.Max(0, count - plan.Skip));
                    return IndexPage.Empty;
                }
                page = equality && !plan.Descending
                    ? automatic.Index.Equal(plan.Minimum!.Value, _items, plan.Skip, plan.Take, default)
                    : automatic.Index.Range(plan.Minimum, plan.Maximum, plan.Descending, _items, plan.Skip, plan.Take, default, plan.IncludeMinimum, plan.IncludeMaximum);
            }
        }
        return page;
    }

    private AutomaticIndex GetAutomaticIndex<T>(PropertyInfo property)
    {
        (Type, PropertyInfo property) key = (typeof(T), property);
        if (_automaticIndexes.TryGetValue(key, out AutomaticIndex? automatic)) return automatic;
        if (!_automaticIndexGroups.TryGetValue(typeof(T), out AutomaticIndexGroup? group))
        {
            group = new AutomaticIndexGroup(static json => JsonUtil.Deserialize<T>(json));
            _automaticIndexGroups.Add(typeof(T), group);
        }
        automatic = CreateAutomaticIndex<T>(property);
        foreach ((string id, string json) in _items) automatic.Set(id, group.Deserialize(json));
        _automaticIndexes.Add(key, automatic);
        group.Indexes.Add(automatic);
        return automatic;
    }

    private static AutomaticIndex CreateAutomaticIndex<T>(PropertyInfo property)
    {
        ParameterExpression parameter = Expression.Parameter(typeof(object));
        MemberExpression value = Expression.Property(Expression.Convert(parameter, typeof(T)), property);
        // Build the scalar key directly so indexing value-type properties never boxes each document's value.
        bool text = property.PropertyType == typeof(string);
        bool boolean = property.PropertyType == typeof(bool);
        Expression kind = text
            ? Expression.Condition(Expression.Equal(value, Expression.Constant(null, typeof(string))), Expression.Constant(0), Expression.Constant(3))
            : Expression.Constant(boolean ? 1 : 2);
        Expression number = text ? Expression.Constant(0m)
            : boolean ? Expression.Condition(value, Expression.Constant(1m), Expression.Constant(0m))
            : Expression.Convert(value, typeof(decimal));
        NewExpression key = Expression.New(typeof(IndexKey).GetConstructor([typeof(int), typeof(decimal), typeof(string)])!,
            kind, number, text ? value : Expression.Constant(null, typeof(string)));
        var getter = Expression.Lambda<Func<object, IndexKey?>>(Expression.Convert(key, typeof(IndexKey?)), parameter).Compile();
        return new AutomaticIndex(getter);
    }

    private void PrepareAutomaticIndexes<T>(ReadOnlySpan<IndexFilter> filters, PropertyInfo? order)
    {
        List<(PropertyInfo Property, AutomaticIndex Index)>? pending = null;
        for (var i = 0; i < filters.Length + (order is null ? 0 : 1); i++)
        {
            PropertyInfo property = i < filters.Length ? filters[i].Property : order!;
            if (_automaticIndexes.ContainsKey((typeof(T), property))) continue;
            var duplicate = false;
            if (pending is not null)
                foreach ((PropertyInfo Property, AutomaticIndex Index) entry in pending) if (entry.Property == property) { duplicate = true; break; }
            if (duplicate) continue;
            (pending ??= new()).Add((property, CreateAutomaticIndex<T>(property)));
        }
        if (pending is null) return;
        if (!_automaticIndexGroups.TryGetValue(typeof(T), out AutomaticIndexGroup? group))
        {
            group = new AutomaticIndexGroup(static json => JsonUtil.Deserialize<T>(json));
            _automaticIndexGroups.Add(typeof(T), group);
        }
        // All indexes needed by this cold plan share a single document-deserialization pass.
        foreach ((string id, string json) in _items)
        {
            object? value = group.Deserialize(json);
            foreach ((PropertyInfo Property, AutomaticIndex Index) entry in pending) entry.Index.Set(id, value);
        }
        foreach ((PropertyInfo Property, AutomaticIndex Index) entry in pending)
        {
            _automaticIndexes.Add((typeof(T), entry.Property), entry.Index);
            group.Indexes.Add(entry.Index);
        }
    }

    private void UpdateAutomaticIndexes(string id, string json)
    {
        foreach (AutomaticIndexGroup group in _automaticIndexGroups.Values)
        {
            object? value = group.Deserialize(json);
            foreach (AutomaticIndex index in group.Indexes) index.Set(id, value);
        }
    }

}

