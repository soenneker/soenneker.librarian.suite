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
        // Small batches are common in optimistic transactions; a bounded scan avoids a hash table per operation.
        HashSet<(string, string)>? addresses = writeArray.Length > 8 ? new HashSet<(string, string)>(writeArray.Length, AddressComparer.Instance) : null;
        for (int i = 0; i < writeArray.Length; i++)
        {
            LibrarianWrite write = writeArray[i];
            if (write is null) throw new ArgumentException("Writes cannot contain null.", nameof(writes));
            Validate(write.Container, write.Id, addresses);
            if (addresses is null)
                for (int j = 0; j < i; j++)
                    RejectDuplicate(write.Container, write.Id, writeArray[j].Container, writeArray[j].Id);
        }
        addresses?.Clear();
        if (conditionArray.Length > 8) addresses ??= new HashSet<(string, string)>(conditionArray.Length, AddressComparer.Instance);
        for (int i = 0; i < conditionArray.Length; i++)
        {
            LibrarianCondition condition = conditionArray[i];
            if (condition is null) throw new ArgumentException("Conditions cannot contain null.", nameof(conditions));
            Validate(condition.Container, condition.Id, addresses);
            if (addresses is null)
                for (int j = 0; j < i; j++)
                    RejectDuplicate(condition.Container, condition.Id, conditionArray[j].Container, conditionArray[j].Id);
        }
        Writes = Array.AsReadOnly(writeArray);
        Conditions = Array.AsReadOnly(conditionArray);
    }

    /// <summary>Writes to apply after every condition succeeds.</summary>
    public IReadOnlyList<LibrarianWrite> Writes { get; }
    /// <summary>Conditions evaluated against the state immediately preceding the commit.</summary>
    public IReadOnlyList<LibrarianCondition> Conditions { get; }

    private static void RejectDuplicate(string container, string id, string previousContainer, string previousId)
    {
        if (StringComparer.Ordinal.Equals(container, previousContainer) && StringComparer.OrdinalIgnoreCase.Equals(id, previousId))
            throw new ArgumentException($"Duplicate document '{id}' in container '{container}'.");
    }

    private static void Validate(string container, string id, HashSet<(string, string)>? addresses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        ArgumentNullException.ThrowIfNull(id);
        if (addresses is not null && !addresses.Add((container, id)))
            throw new ArgumentException($"Duplicate document '{id}' in container '{container}'.");
    }

    private sealed class AddressComparer : IEqualityComparer<(string Container, string Id)>
    {
        internal static readonly AddressComparer Instance = new();
        public bool Equals((string Container, string Id) x, (string Container, string Id) y) =>
            StringComparer.Ordinal.Equals(x.Container, y.Container) && StringComparer.OrdinalIgnoreCase.Equals(x.Id, y.Id);
        public int GetHashCode((string Container, string Id) value) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.Container), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Id));
    }
}
