using System;
using System.Collections.Generic;

namespace Soenneker.Librarian.Mongo;

public sealed class MongoMetadata
{
    public string Version { get; set; } = Guid.NewGuid().ToString("N");
    public Dictionary<string, List<string>> Indexes { get; set; } = new(StringComparer.Ordinal);
    public IEnumerable<string> Paths(string container) => Indexes.TryGetValue(container, out List<string>? paths) ? paths : [];
}
