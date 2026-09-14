using System.Linq.Expressions;
using System.Reflection;

namespace Soenneker.Librarian.Core.Indexes;

internal struct IndexFilter(PropertyInfo property)
{
    internal PropertyInfo Property = property;
    internal IndexKey? Minimum;
    internal IndexKey? Maximum;
    internal bool IncludeMinimum = true;
    internal bool IncludeMaximum = true;
    internal readonly bool Empty => Minimum is { } min && Maximum is { } max
        && (min.CompareTo(max) > 0 || min == max && (!IncludeMinimum || !IncludeMaximum));

    internal void Apply(IndexKey key, ExpressionType operation)
    {
        if (operation is ExpressionType.Equal or ExpressionType.GreaterThanOrEqual or ExpressionType.GreaterThan)
        {
            bool inclusive = operation != ExpressionType.GreaterThan;
            if (Minimum is null || Minimum.Value.CompareTo(key) < 0) { Minimum = key; IncludeMinimum = inclusive; }
            else if (Minimum.Value == key) IncludeMinimum &= inclusive;
        }
        if (operation is ExpressionType.Equal or ExpressionType.LessThanOrEqual or ExpressionType.LessThan)
        {
            bool inclusive = operation != ExpressionType.LessThan;
            if (Maximum is null || Maximum.Value.CompareTo(key) > 0) { Maximum = key; IncludeMaximum = inclusive; }
            else if (Maximum.Value == key) IncludeMaximum &= inclusive;
        }
    }

    internal readonly bool Matches(IndexKey key)
    {
        if (Minimum is { } min && (key.CompareTo(min) < 0 || !IncludeMinimum && key == min)) return false;
        if (Maximum is { } max && (key.CompareTo(max) > 0 || !IncludeMaximum && key == max)) return false;
        return true;
    }
}
