using System.Collections.Generic;

namespace Soenneker.Librarian.Core;

internal sealed record LibrarianContainerState(Dictionary<string, LibrarianPreparedWrite> Writes);
