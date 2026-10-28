using System.Collections.Generic;
using Microsoft.Azure.Cosmos;

namespace Soenneker.Librarian.Cosmos;

internal static class CosmosFeedPage
{
    internal static List<T> Materialize<T>(FeedResponse<T> page)
    {
        // SDK pages own their deserialized resources; transfer an existing list without copying it.
        if (page.Resource is List<T> list) return list;
        var result = new List<T>(page.Count);
        foreach (T item in page) result.Add(item);
        return result;
    }
}
