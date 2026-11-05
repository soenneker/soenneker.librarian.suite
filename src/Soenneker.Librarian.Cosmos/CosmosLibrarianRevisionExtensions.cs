using System;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Cosmos.Repository.Abstract;
using Soenneker.Cosmos.Repository.Dtos;
using Soenneker.Documents.Document;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Cosmos;

/// <summary>Application revision checks for Cosmos-compatible repositories.</summary>
public static class CosmosLibrarianRevisionExtensions
{
    /// <summary>Atomically checks and advances a section revision with its mutation; returns null for a missing document.</summary>
    /// <remarks>
    /// Uses the repository's bounded ETag conflict retries. The revision is checked again on every attempt before
    /// invoking the mutation. A mismatch throws <see cref="LibrarianRevisionConflictException"/>; it is never retried
    /// as a fresh edit. Every writer of the section must advance its revision. Callbacks must preserve identity,
    /// be free of external side effects, and tolerate repeated execution after confirmed version conflicts.
    /// Duplicate requests are rejected; an uncertain write outcome must be resolved by reading the document.
    /// </remarks>
    public static ValueTask<T?> MutateItemAtRevision<T>(this ICosmosRepository<T> repository, string id,
        long expectedRevision, Func<T, long> getRevision, Action<T, long> setRevision, Action<T> mutation,
        int maxAttempts = 5, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default) where T : Document
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(getRevision);
        ArgumentNullException.ThrowIfNull(setRevision);
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRevision);
        long nextRevision = checked(expectedRevision + 1);
        return repository.MutateItem(id, current =>
        {
            long actual = getRevision(current);
            if (actual != expectedRevision)
                throw new LibrarianRevisionConflictException(expectedRevision, actual);
            string? originalId = current.Id;
            mutation(current);
            setRevision(current, nextRevision);
            if (!string.Equals(current.Id, originalId, StringComparison.Ordinal))
                throw new ArgumentException("A revision mutation must preserve document identity.", nameof(mutation));
            if (getRevision(current) != nextRevision)
                throw new ArgumentException("The revision setter must store the supplied revision.", nameof(setRevision));
            return true;
        }, maxAttempts, readOptions, cancellationToken);
    }
}
