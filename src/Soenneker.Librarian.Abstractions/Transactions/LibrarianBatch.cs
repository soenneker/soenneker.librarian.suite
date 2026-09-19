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
        HashSet<(string, string)>? addresses = writeArray.Length > 1 ? new(writeArray.Length, AddressComparer.Instance) : null;
        foreach (LibrarianWrite write in writeArray)
        {
            if (write is null) throw new ArgumentException("Writes cannot contain null.", nameof(writes));
            Validate(write.Container, write.Id, addresses);
        }
        addresses?.Clear();
        if (conditionArray.Length > 1) addresses ??= new(conditionArray.Length, AddressComparer.Instance);
        foreach (LibrarianCondition condition in conditionArray)
        {
            if (condition is null) throw new ArgumentException("Conditions cannot contain null.", nameof(conditions));
            Validate(condition.Container, condition.Id, addresses);
        }
        Writes = Array.AsReadOnly(writeArray);
        Conditions = Array.AsReadOnly(conditionArray);
    }

    /// <summary>Writes to apply after every condition succeeds.</summary>
    public IReadOnlyList<LibrarianWrite> Writes { get; }
    /// <summary>Conditions evaluated against the state immediately preceding the commit.</summary>
    public IReadOnlyList<LibrarianCondition> Conditions { get; }

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
