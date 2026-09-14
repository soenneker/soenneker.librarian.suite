using System;
using System.Collections.Generic;
using System.Linq;

namespace Soenneker.Librarian.Abstractions.Transactions;

/// <summary>An immutable set of document preconditions and writes, committed together by a database.</summary>
/// <remarks>Each document may have at most one condition and one write. Unconditioned writes are upserts.
/// Values are compared as raw text, not JSON structure. Conditions do not prevent an ABA change back to the same text;
/// include a revision or fencing value in your documents when that distinction matters.</remarks>
public sealed class LibrarianBatch
{
    /// <summary>Copies and validates the batch. Input collections can be reused after construction.</summary>
    public LibrarianBatch(IEnumerable<LibrarianWrite> writes, IEnumerable<LibrarianCondition>? conditions = null)
    {
        ArgumentNullException.ThrowIfNull(writes);
        LibrarianWrite[] writeArray = writes.ToArray();
        LibrarianCondition[] conditionArray = conditions?.ToArray() ?? [];
        Validate(writeArray.Select(write => write is null ? throw new ArgumentException("Writes cannot contain null.") : (write.Container, write.Id)));
        Validate(conditionArray.Select(condition => condition is null ? throw new ArgumentException("Conditions cannot contain null.") : (condition.Container, condition.Id)));
        Writes = Array.AsReadOnly(writeArray);
        Conditions = Array.AsReadOnly(conditionArray);
    }

    /// <summary>Writes to apply after every condition succeeds.</summary>
    public IReadOnlyList<LibrarianWrite> Writes { get; }
    /// <summary>Conditions evaluated against the state immediately preceding the commit.</summary>
    public IReadOnlyList<LibrarianCondition> Conditions { get; }

    private static void Validate(IEnumerable<(string Container, string Id)> addresses)
    {
        var containers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach ((string container, string id) in addresses)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(container);
            ArgumentNullException.ThrowIfNull(id);
            if (!containers.TryGetValue(container, out HashSet<string>? ids)) containers.Add(container, ids = new(StringComparer.OrdinalIgnoreCase));
            if (!ids.Add(id)) throw new ArgumentException($"Duplicate document '{id}' in container '{container}'.");
        }
    }
}
