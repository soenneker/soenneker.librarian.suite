using System.Collections.Generic;
using Soenneker.Librarian.Core.Indexes;

namespace Soenneker.Librarian.Core;

internal sealed record LibrarianContainerState(Dictionary<string, LibrarianPreparedWrite> Writes);

internal sealed record LibrarianPreparedWrite(string? Value, IndexKey?[] Keys, IndexKey?[] AutomaticKeys);
