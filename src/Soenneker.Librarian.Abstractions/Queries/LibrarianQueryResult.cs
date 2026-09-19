using System.Collections.Generic;

namespace Soenneker.Librarian.Abstractions.Queries;

/// <summary>A materialized index query page and its execution statistics.</summary>
/// <typeparam name="T">The returned document type.</typeparam>
public sealed class LibrarianQueryResult<T>
{
    /// <summary>The documents in the requested page.</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>The JSON field path of the index used. Indexed queries never fall back to a document scan.</summary>
    public required string Index { get; init; }

    /// <summary>The number of matching index entries fetched. PostgreSQL reports entries returned to the client; SQL OFFSET may visit skipped entries on the server.</summary>
    public required int IndexEntriesExamined { get; init; }

    /// <summary>The number of documents deserialized to produce this page.</summary>
    public required int DocumentsDeserialized { get; init; }
}
