using System;

namespace Soenneker.Librarian.Core.Indexes;

internal sealed class AutomaticIndex(Func<object, IndexKey?> extract)
{
    internal readonly DocumentIndex Index = new("automatic");

    internal IndexKey? Extract(object? value)
    {
        try { return value is null ? null : extract(value); }
        catch (Exception) { return null; } // Match BuildQueryable's invalid-document behavior without rejecting raw writes.
    }

    internal void Set(string id, object? value) => Index.Set(id, Extract(value));
}
