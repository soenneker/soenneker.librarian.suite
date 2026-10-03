using System;
using Soenneker.Librarian.Abstractions.Transactions;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Librarian.Abstractions;

/// <summary>
/// A document database implemented by a memory, filesystem, Redis, PostgreSQL, MongoDB, Cosmos DB, or Cloudflare provider.
/// </summary>
public interface ILibrarianDatabase : IAsyncDisposable
{
    /// <summary>Atomically checks document conditions and applies a batch across containers.</summary>
    /// <returns>True if committed; false when a condition fails. Failures and cancellation throw.</returns>
    /// <remarks>All built-in providers support this operation. Existing third-party providers may throw NotSupportedException.
    /// No writes are applied for a failed condition or validation error. Ordinary reads and writes participate in the same
    /// coordination boundary. Separate read calls are not a snapshot; protect decisions with conditions on every document read.
    /// Memory coordinates within one database instance. Filesystem coordinates within its single owner and persists before
    /// publishing the new state. Redis coordinates across instances with version-checked atomic scripts and database-wide hash slots.
    /// Cancellation is checked before commit; an operation already dispatched may commit. A transport failure can leave
    /// the Redis commit outcome unknown; callers must reconcile authoritative state before retrying non-idempotent work.</remarks>
    /// <remarks>PostgreSQL uses a server transaction and a logical database row lock shared by all writes across instances.
    /// Reads use statement snapshots. A cancelled or interrupted PostgreSQL commit can also have an unknown outcome.</remarks>
    /// <remarks>D1 and R2 coordinate within a single owner of each stored snapshot. Batches replace the complete snapshot
    /// before publication and include pending ordinary writes. Other instances must not share the same storage address.
    /// A transport failure or cancellation after dispatch can leave the remote commit outcome unknown.</remarks>
    /// <remarks>MongoDB commits native documents and a database-wide version together. Conditions and writes
    /// coordinate across provider instances; conflicts retry up to 16 times before throwing TimeoutException.
    /// MongoDB requires a replica set or sharded cluster supporting transactions. Cosmos DB uses one logical partition per
    /// Librarian database key in a container partitioned by /partitionKey. A lazily created Cosmos stored procedure checks
    /// conditions and applies writes transactionally, including conditions on documents not otherwise written. Confirmed
    /// transaction conflicts retry up to five times; other failures propagate. Cosmos request size, execution time, storage,
    /// and partition throughput limits apply. Exceeding the procedure's execution budget rolls back the entire operation.
    /// Transport failures after dispatch may leave either provider's commit outcome unknown. Reconcile before retrying.
    /// Only Librarian providers may modify their stored documents and metadata.</remarks>
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
    /// D1 and R2 keep documents and indexes in memory and save complete snapshots explicitly, on unload, and on asynchronous
    /// disposal; they do not run periodic saves. Failed saves retain pending changes for retry. Indexes are rebuilt after reload.
    /// D1 stores one snapshot row in librarian_snapshots, limited to 1,900,000 UTF-8 bytes including the logical name.
    /// R2 stores one JSON object. Both require an existing remote database or bucket and a single owner per snapshot.
    /// MongoDB and Cosmos DB also write immediately; Save and MarkDirty perform no I/O. Unload releases local handles only.
    /// Configure Librarian:Mongo:ConnectionString, DatabaseName, Key, and optionally CollectionName (default librarian).
    /// Cosmos uses ICosmosContainerUtil and the existing Azure:Cosmos configuration: Endpoint, AccountKey, DatabaseName,
    /// DatabaseThroughput, and DatabaseThroughputType, plus Environment for the shared client utility.
    /// The first GetContainer or Execute ensures the database and physical container through the Cosmos utilities.
    /// Azure:Cosmos:EnsureDatabaseOnFirstUse and EnsureContainerOnFirstUse default to true; disabling them requires
    /// pre-provisioned resources. Optional Librarian:Cosmos:ContainerName and Key both default to librarian.
    /// Supplying a Cosmos Container directly assumes it is already provisioned. Shared Cosmos clients remain utility-owned.
    /// Both offer default/keyed singleton and scoped DI registrations; prefer singleton clients for connection reuse.
    /// Caller-supplied MongoDB databases and Cosmos containers retain ownership of their clients after provider disposal.
    /// The MongoDB and Cosmos SDK packages are not advertised as Native AOT compatible.
    /// </remarks>
    ValueTask Save(CancellationToken cancellationToken = default);

    /// <summary>
    /// Unloads and disposes the named container. Filesystem, D1, and R2 providers save first; memory providers discard data; Redis and PostgreSQL providers release only the local handle.
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
