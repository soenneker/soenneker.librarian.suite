using System;
using System.Collections.Generic;
using System.Linq;

namespace Soenneker.Librarian.Abstractions.Transactions;

/// <summary>An immutable set of document preconditions and writes, committed together by a database.</summary>
/// <remarks>Unconditioned writes are upserts. MongoDB and Cosmos accept versions and create-only writes;
/// other providers accept raw-value conditions. Native providers reject raw-value conditions.
/// IDs differing only by case are conservatively treated as duplicates during batch validation.</remarks>
public sealed class LibrarianBatch
{
    /// <summary>Copies and validates the batch. Input collections can be reused after construction.</summary>
    public LibrarianBatch(IEnumerable<LibrarianWrite> writes, IEnumerable<LibrarianCondition>? conditions = null)
    {
        ArgumentNullException.ThrowIfNull(writes);
        LibrarianWrite[] writeArray = writes.ToArray();
        LibrarianCondition[] conditionArray = conditions?.ToArray() ?? [];
        var addresses = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (LibrarianWrite write in writeArray)
        {
            ArgumentNullException.ThrowIfNull(write);
            Validate(write.Container, write.Id, addresses);
            if (write.ExpectedVersion is not null)
                ArgumentException.ThrowIfNullOrWhiteSpace(write.ExpectedVersion);
            if (write.CreateOnly && (write.Value is null || write.ExpectedVersion is not null))
                throw new ArgumentException("Create-only writes require a document and cannot specify a version.",
                    nameof(writes));
        }

        addresses.Clear();
        foreach (LibrarianCondition condition in conditionArray)
        {
            ArgumentNullException.ThrowIfNull(condition);
            Validate(condition.Container, condition.Id, addresses);
        }

        Writes = Array.AsReadOnly(writeArray);
        Conditions = Array.AsReadOnly(conditionArray);
    }

    /// <summary>Writes to apply after every condition succeeds.</summary>
    public IReadOnlyList<LibrarianWrite> Writes { get; }

    /// <summary>Raw-value conditions. Native MongoDB/Cosmos batches use ExpectedVersion or CreateOnly on writes instead.</summary>
    public IReadOnlyList<LibrarianCondition> Conditions { get; }

    /// <summary>Rejects concurrency options unsupported by this provider before it performs any writes.</summary>
    public void ValidateConcurrency(bool supportsVersions)
    {
        if (supportsVersions && Conditions.Count != 0)
            throw new NotSupportedException(
                "Native batches use ExpectedVersion or CreateOnly on each write, not raw-value conditions.");
        if (!supportsVersions && Writes.Any(write => write.ExpectedVersion is not null || write.CreateOnly))
            throw new NotSupportedException("This provider does not support versioned or create-only batch writes.");
    }

    private static void Validate(string container, string id, Dictionary<string, HashSet<string>> addresses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        ArgumentNullException.ThrowIfNull(id);
        if (!addresses.TryGetValue(container, out HashSet<string>? ids))
            addresses.Add(container, ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (!ids.Add(id))
            throw new ArgumentException($"Duplicate document '{id}' in container '{container}'.");
    }
}