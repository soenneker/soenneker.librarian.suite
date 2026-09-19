using System;
using Soenneker.Librarian.Abstractions.Transactions;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Librarian.Abstractions;

/// <summary>
/// A document database implemented by a memory, filesystem, Redis, or PostgreSQL provider.
/// </summary>
public interface ILibrarianDatabase : IAsyncDisposable
{
    /// <summary>Atomically checks document conditions and applies a batch across containers.</summary>
    /// <returns>True if committed; false when a condition fails. Failures and cancellation throw.</returns>
    /// <remarks>All built-in providers support this operation. Existing third-party providers may throw NotSupportedException.
    /// No writes are applied for a failed condition or validation error. Ordinary reads and writes participate in the same
    /// coordination boundary. Separate read calls are not a snapshot; protect decisions with conditions on every document read.
    /// Memory coordinates within one database instance. Filesystem coordinates within its single owner and persists before
    /// publishing the new state. Redis coordinates across instances with conditional transactions and database-wide hash slots.
    /// Cancellation is checked before commit; an operation already dispatched may commit. A transport failure can leave
    /// the Redis commit outcome unknown; callers must reconcile authoritative state before retrying non-idempotent work.</remarks>
    /// <remarks>PostgreSQL uses a server transaction and a logical database row lock shared by all writes across instances.
    /// Reads use statement snapshots. A cancelled or interrupted PostgreSQL commit can also have an unknown outcome.</remarks>
    ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This provider does not support atomic batches.");

    /// <summary>
    /// Marks a container as dirty, indicating it needs to be saved. Typically, is not needed to be called manually, but available.
    /// </summary>
    /// <param name="containerName">The name of the container.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A ValueTask representing the asynchronous operation.</returns>
    /// <remarks>Records an already committed in-memory mutation. Cancellation does not suppress dirty tracking. Redis and PostgreSQL write immediately and perform no dirty tracking.</remarks>
    ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an owned container by its case-sensitive name, creating it when needed.
    /// </summary>
    /// <param name="containerName">Name of the container to target.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A ValueTask containing the requested LibrarianContainer.</returns>
    ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves pending changes when supported by the provider; memory databases perform no disk I/O.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    /// <remarks>
    /// The filesystem provider writes through a temporary sibling file before replacing the database. Load, serialization, write, and cancellation
    /// failures propagate to the caller and leave pending changes eligible for retry. Background saves log failures and retry.
    /// Redis and PostgreSQL mutations commit immediately; Save is a no-op and failures propagate from the mutation itself.
    /// Redis commands already dispatched are awaited even when the token is cancelled.
    /// Await filesystem database disposal to flush pending changes; stop container operations before disposing the database.
    /// </remarks>
    ValueTask Save(CancellationToken cancellationToken = default);

    /// <summary>
    /// Unloads and disposes the named container. Filesystem providers save first; memory providers discard data; Redis and PostgreSQL providers release only the local handle.
    /// </summary>
    /// <param name="containerName">Name of the container to target.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>true if the container was unloaded; otherwise, false.</returns>
    /// <remarks>
    /// Stop operations on the container before unloading it. Existing references become invalid; retrieve a new reference
    /// with GetContainer to use it again. A failed persistence save prevents unloading; memory data is discarded.
    /// </remarks>
    ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default);
}
