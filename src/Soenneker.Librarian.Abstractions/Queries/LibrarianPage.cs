using System.Collections.Generic;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>One server page. A non-null continuation token means more pages may remain, even when Items is empty.</summary>
public sealed record LibrarianPage<T>(IReadOnlyList<T> Items, string? ContinuationToken);
