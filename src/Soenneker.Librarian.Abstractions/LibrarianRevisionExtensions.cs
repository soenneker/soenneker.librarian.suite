using System;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Documents.Document;

namespace Soenneker.Librarian.Abstractions;

/// <summary>Application revision checks layered on the repository's atomic versioned mutations.</summary>
public static class LibrarianRevisionExtensions
{
    /// <summary>Applies a mutation only at the expected application revision, advancing it by one in the same write.</summary>
    /// <remarks>
    /// The selectors identify an application-owned revision stored in the document, for example a revision per editable section.
    /// Every writer to that section must advance it. Unrelated document conflicts are retried by the repository;
    /// a changed section revision throws <see cref="LibrarianRevisionConflictException"/> without invoking the mutation.
    /// Replayed requests are conflicts, not successful acknowledgements. All callbacks must be deterministic,
    /// preserve document identity, and have no external side effects. Uncertain write failures are not retried.
    /// </remarks>
    public static ValueTask<LibrarianItem<T>> MutateItemAtRevision<T>(this ILibrarianRepository<T> repository, string id,
        long expectedRevision, Func<T, long> getRevision, Action<T, long> setRevision, Action<T> mutation,
        int maxAttempts = 5, CancellationToken cancellationToken = default) where T : Document
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
            return current;
        }, maxAttempts, cancellationToken);
    }
}
