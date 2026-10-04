using Soenneker.Extensions.ValueTask;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Abstractions;

/// <summary>Typed versioned operations using registered Librarian JSON contracts.</summary>
public static class LibrarianVersionExtensions
{
    /// <summary>Reads a typed document and its version, or null when absent.</summary>
    public static async ValueTask<LibrarianItem<T>?> GetItemWithVersion<T>(this ILibrarianContainer container, string id,
        CancellationToken cancellationToken = default)
    {
        LibrarianItem<string>? item = await container.GetItemWithVersion(id, cancellationToken).NoSync();
        return item is null ? null : new LibrarianItem<T>(LibrarianJson.Deserialize<T>(item.Document)!, item.Version);
    }

    /// <summary>Conditionally replaces a typed document, returning its new version or null on conflict or absence.</summary>
    public static async ValueTask<LibrarianItem<T>?> UpdateItemIfVersion<T>(this ILibrarianContainer container, string id, T document,
        string version, CancellationToken cancellationToken = default)
    {
        LibrarianItem<string>? item = await container.UpdateItemIfVersion(id, LibrarianJson.Serialize(document), version, cancellationToken).NoSync();
        return item is null ? null : new LibrarianItem<T>(document, item.Version);
    }

    /// <summary>Reads, transforms, and conditionally replaces an existing document, retrying only confirmed conflicts.</summary>
    /// <remarks>The callback can run multiple times and must not perform external side effects. Missing documents throw
    /// KeyNotFoundException; exhausted conflicts throw LibrarianConcurrencyException. Transport failures propagate without retry.</remarks>
    public static async ValueTask<LibrarianItem<T>> MutateItem<T>(this ILibrarianContainer container, string id, Func<T, T> mutation,
        int maxAttempts = 5, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibrarianItem<T> current = await container.GetItemWithVersion<T>(id, cancellationToken).NoSync()
                ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");
            T changed = mutation(current.Document);
            cancellationToken.ThrowIfCancellationRequested();
            LibrarianItem<T>? result = await container.UpdateItemIfVersion(id, changed, current.Version, cancellationToken).NoSync();
            if (result is not null) return result;
        }
        throw new LibrarianConcurrencyException($"Document '{id}' changed during all {maxAttempts} mutation attempts.");
    }
}
