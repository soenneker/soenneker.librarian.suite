using System.Collections.Generic;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Core.Indexes;

internal sealed class QueryTypeEqualityComparer(QueryType type) : IEqualityComparer<object?>
{
    public new bool Equals(object? left, object? right) => type.Equal(left, right);
    public int GetHashCode(object? value) => value?.GetHashCode() ?? 0;
}
