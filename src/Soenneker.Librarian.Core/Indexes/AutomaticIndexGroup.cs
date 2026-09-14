using System;
using System.Collections.Generic;

namespace Soenneker.Librarian.Core.Indexes;

internal sealed class AutomaticIndexGroup(Func<string, object?> deserialize)
{
    internal readonly List<AutomaticIndex> Indexes = new();

    internal object? Deserialize(string json)
    {
        try { return deserialize(json); }
        catch (Exception) { return null; }
    }
}
