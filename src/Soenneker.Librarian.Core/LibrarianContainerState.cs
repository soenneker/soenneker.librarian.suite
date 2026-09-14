using System.Collections.Concurrent;
using System.Collections.Generic;
using Soenneker.Librarian.Core.Indexes;

namespace Soenneker.Librarian.Core;

internal sealed record LibrarianContainerState(ConcurrentDictionary<string, string> Items,
    Dictionary<string, DocumentIndex> Indexes, IndexKey?[] PreparedKeys);
