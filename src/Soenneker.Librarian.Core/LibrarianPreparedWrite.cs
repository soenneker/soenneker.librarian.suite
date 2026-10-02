using Soenneker.Librarian.Core.Indexes;

namespace Soenneker.Librarian.Core;

internal readonly record struct LibrarianPreparedWrite(string? Value, IndexKey?[] Keys, IndexKey?[] AutomaticKeys);
