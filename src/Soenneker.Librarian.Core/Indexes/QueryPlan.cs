using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Soenneker.Librarian.Core.Indexes;

// Translate only a leading, fully understood sequence. Everything after Prefix runs as normal LINQ.
internal sealed class QueryPlan
{
    private static readonly ConditionalWeakTable<PropertyInfo, PropertySupport> _properties = new();
    internal PropertyInfo Property = null!;
    internal Expression Prefix = null!;
    internal IndexKey? Minimum;
    internal IndexKey? Maximum;
    internal bool IncludeMinimum = true;
    internal bool IncludeMaximum = true;
    internal bool Descending;
    internal int Skip;
    internal int Take = int.MaxValue;
    internal bool Empty;
    internal bool CountOnly;
    internal int Count;
    internal List<IndexFilter>? AdditionalFilters;
    internal PropertyInfo? OrderProperty;
    internal LambdaExpression? ResidualPredicate;
    internal IndexFilter PrimaryFilter => new(Property)
    {
        Minimum = Minimum, Maximum = Maximum, IncludeMinimum = IncludeMinimum, IncludeMaximum = IncludeMaximum
    };

    internal static QueryPlan? CreatePredicate(Expression source, LambdaExpression predicate, IQueryProvider provider)
    {
        if (predicate.Parameters.Count != 1) return null;
        QueryPlan? plan = source is ConstantExpression { Value: IQueryable query } && query.Provider == provider
            ? new QueryPlan { Prefix = source } : Create(source, provider);
        // Predicates cannot move ahead of an existing page.
        if (plan is null || plan.ResidualPredicate is not null || plan.Prefix != source || plan.Skip != 0 || plan.Take != int.MaxValue) return null;
        return plan.TryFilter(predicate.Body, predicate.Parameters[0]) ? plan : null;
    }

    internal static QueryPlan? Create(Expression expression, IQueryProvider provider)
    {
        var count = 0;
        Expression root = expression;
        while (root is MethodCallExpression call && call.Method.DeclaringType == typeof(Queryable))
        {
            count++;
            root = call.Arguments[0];
        }
        if (count == 0 || root is not ConstantExpression { Value: IQueryable query } || query.Provider != provider)
            return null;

        InlineBuffer<MethodCallExpression> buffer = default;
        Span<MethodCallExpression> calls = count <= 8 ? buffer : new MethodCallExpression[count];
        root = expression;
        for (int i = count - 1; i >= 0; i--)
        {
            calls[i] = (MethodCallExpression)root;
            root = calls[i].Arguments[0];
        }
        var plan = new QueryPlan();
        var paged = false;
        var ordered = false;
        for (var i = 0; i < count; i++)
        {
            MethodCallExpression call = calls[i];
            string name = call.Method.Name;
            if (name == nameof(Queryable.Where) && !paged && call.Arguments[1] is UnaryExpression { Operand: LambdaExpression predicate }
                && predicate.Parameters.Count == 1 && plan.TryFilter(predicate.Body, predicate.Parameters[0]))
            {
                plan.Prefix = call;
                continue;
            }
            if (name == nameof(Queryable.Where) && !paged
                && call.Arguments[1] is UnaryExpression { Operand: LambdaExpression residual } && residual.Parameters.Count == 1)
            {
                var accepted = 0;
                plan.AddLeadingFilters(residual.Body, residual.Parameters[0], ref accepted);
                if (accepted > 0)
                {
                    plan.Prefix = call;
                    plan.ResidualPredicate = residual;
                    return plan;
                }
                break;
            }
            if ((name == nameof(Queryable.OrderBy) || name == nameof(Queryable.OrderByDescending)) && !ordered && !paged
                && call.Arguments.Count == 2 && call.Arguments[1] is UnaryExpression { Operand: LambdaExpression order }
                && GetProperty(order.Body, order.Parameters[0]) is { } property && property.PropertyType != typeof(string))
            {
                // ThenBy needs the original ordered sequence, not an array containing already sorted rows.
                if (i + 1 < count && calls[i + 1].Method.Name is nameof(Queryable.ThenBy) or nameof(Queryable.ThenByDescending))
                    break;
                plan.Property ??= property;
                plan.OrderProperty = property;
                plan.Descending = name == nameof(Queryable.OrderByDescending);
                ordered = true;
                plan.Prefix = call;
                continue;
            }
            if (plan.Property is not null && (name == nameof(Queryable.Skip) || name == nameof(Queryable.Take))
                && call.Arguments[1] is ConstantExpression { Value: int amount })
            {
                plan.ApplyPage(name, amount);
                paged = true;
                plan.Prefix = call;
                continue;
            }
            break;
        }
        return plan.Prefix is null ? null : plan;
    }

    internal void ApplyPage(string method, int amount)
    {
        amount = Math.Max(0, amount);
        if (method == nameof(Queryable.Take)) Take = Math.Min(Take, amount);
        else
        {
            int consumed = Math.Min(Take, amount);
            Skip = (int)Math.Min(int.MaxValue, (long)Skip + consumed);
            Take -= consumed;
        }
    }

    private bool TryFilter(Expression expression, ParameterExpression parameter)
    {
        (PropertyInfo Property, IndexKey? Minimum, IndexKey? Maximum, bool IncludeMinimum, bool IncludeMaximum, bool Empty) saved = (Property, Minimum, Maximum, IncludeMinimum, IncludeMaximum, Empty);
        List<IndexFilter>? savedAdditional = AdditionalFilters;
        int count = savedAdditional?.Count ?? 0;
        InlineBuffer<IndexFilter> buffer = default;
        Span<IndexFilter> savedFilters = count <= 8 ? buffer : new IndexFilter[count];
        if (savedAdditional is not null) CollectionsMarshal.AsSpan(savedAdditional).CopyTo(savedFilters);
        if (Filter(expression, parameter)) return true;
        (Property, Minimum, Maximum, IncludeMinimum, IncludeMaximum, Empty) = saved;
        AdditionalFilters = savedAdditional;
        if (savedAdditional is not null)
        {
            if (savedAdditional.Count > count) savedAdditional.RemoveRange(count, savedAdditional.Count - count);
            savedFilters[..count].CopyTo(CollectionsMarshal.AsSpan(savedAdditional));
        }
        return false;
    }

    private bool AddLeadingFilters(Expression expression, ParameterExpression parameter, ref int accepted)
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } and)
            return AddLeadingFilters(and.Left, parameter, ref accepted) && AddLeadingFilters(and.Right, parameter, ref accepted);
        if (!TryFilter(expression, parameter)) return false;
        accepted++;
        return true;
    }

    private bool Filter(Expression expression, ParameterExpression parameter)
    {
        if (expression is BinaryExpression { NodeType: ExpressionType.AndAlso } and)
            return Filter(and.Left, parameter) && Filter(and.Right, parameter);
        if (GetProperty(expression, parameter) is { PropertyType: var flagType } flag && flagType == typeof(bool))
            return AddFilter(flag, new IndexKey(1, 1), ExpressionType.Equal);
        if (expression is UnaryExpression { NodeType: ExpressionType.Not } not
            && GetProperty(not.Operand, parameter) is { PropertyType: var notType } negated && notType == typeof(bool))
            return AddFilter(negated, new IndexKey(1), ExpressionType.Equal);
        if (expression is not BinaryExpression binary || binary.NodeType is not (ExpressionType.Equal or ExpressionType.GreaterThanOrEqual or ExpressionType.LessThanOrEqual or ExpressionType.GreaterThan or ExpressionType.LessThan)) return false;
        PropertyInfo? property = GetProperty(binary.Left, parameter);
        Expression valueExpression = binary.Right;
        ExpressionType operation = binary.NodeType;
        if (property is null)
        {
            property = GetProperty(binary.Right, parameter);
            valueExpression = binary.Left;
            operation = operation switch
            {
                ExpressionType.GreaterThan => ExpressionType.LessThan,
                ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
                ExpressionType.LessThan => ExpressionType.GreaterThan,
                ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
                _ => operation
            };
        }
        if (property is null || property.PropertyType == typeof(string) && operation != ExpressionType.Equal
            || !TryValue(valueExpression, out object? value)) return false;
        return AddFilter(property, IndexKey.FromValue(value), operation);
    }

    private bool AddFilter(PropertyInfo property, IndexKey key, ExpressionType operation)
    {
        Property ??= property;
        if (Property == property)
        {
            IndexFilter filter = PrimaryFilter;
            filter.Apply(key, operation);
            Minimum = filter.Minimum;
            Maximum = filter.Maximum;
            IncludeMinimum = filter.IncludeMinimum;
            IncludeMaximum = filter.IncludeMaximum;
            Empty |= filter.Empty;
        }
        else
        {
            AdditionalFilters ??= new();
            var index = -1;
            for (var i = 0; i < AdditionalFilters.Count; i++)
                if (AdditionalFilters[i].Property == property) { index = i; break; }
            IndexFilter filter = index < 0 ? new(property) : AdditionalFilters[index];
            filter.Apply(key, operation);
            if (index < 0) AdditionalFilters.Add(filter);
            else AdditionalFilters[index] = filter;
            Empty |= filter.Empty;
        }
        return true;
    }

    internal static PropertyInfo? GetProperty(Expression expression, ParameterExpression parameter)
    {
        if (expression is not MemberExpression { Member: PropertyInfo property } member || member.Expression != parameter) return null;
        return _properties.GetValue(property, static key => new PropertySupport(IsSupported(key))).Supported ? property : null;
    }

    private static bool IsSupported(PropertyInfo property)
    {
        Type type = property.PropertyType;
        if (type != typeof(int) && type != typeof(long) && type != typeof(decimal) && type != typeof(string) && type != typeof(bool)) return false;
        // Computed getters may have side effects or depend on mutable external state.
        return property.GetMethod is { IsVirtual: false } getter && getter.IsDefined(typeof(CompilerGeneratedAttribute), false)
            && property.DeclaringType!.GetField($"<{property.Name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic) is not null;
    }

    private static bool TryValue(Expression expression, out object? value)
    {
        if (expression is ConstantExpression constant) { value = constant.Value; return true; }
        if (expression is MemberExpression { Member: FieldInfo field, Expression: { } target } && TryValue(target, out object? owner))
        {
            if (owner is null) { value = null; return false; }
            value = field.GetValue(owner);
            return true;
        }
        value = null;
        return false;
    }
}
