using System;

namespace Soenneker.Librarian.Core.Indexes;

internal sealed class AutomaticIndex(Func<object, IndexKey?> extract)
{
    internal readonly DocumentIndex Index = new("automatic");

    internal void Set(string id, object? value)
    {
        IndexKey? key;
        try { key = value is null ? null : extract(value); }
        catch (Exception) { key = null; } // Match BuildQueryable's invalid-document behavior without rejecting raw writes.
        Index.Set(id, key);
    }
}
