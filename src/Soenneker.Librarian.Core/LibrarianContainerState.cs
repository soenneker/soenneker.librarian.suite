using System.Collections.Generic;
using Soenneker.Librarian.Core.Indexes;

namespace Soenneker.Librarian.Core;

internal sealed record LibrarianContainerState(Dictionary<string, string> Items,
    Dictionary<string, DocumentIndex> Indexes, IndexKey?[] PreparedKeys);
