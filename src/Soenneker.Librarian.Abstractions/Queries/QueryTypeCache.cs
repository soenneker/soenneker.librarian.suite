namespace Soenneker.Librarian.Abstractions.Queries;

internal static class QueryTypeCache<T>
{
    internal static readonly QueryType Value = QueryTypes.Add<T>();
}
