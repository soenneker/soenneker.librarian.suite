using System;
using System.Linq;
using Soenneker.Librarian.Abstractions.Transactions;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Librarian.Abstractions;

/// <summary>
/// A document database implemented by a memory, filesystem, secure MAUI, browser, Redis, PostgreSQL, MongoDB, Cosmos DB, CouchDB, or Cloudflare provider.
/// </summary>
public interface ILibrarianDatabase : IAsyncDisposable
{
    /// <summary>Gets a container scoped to an explicit, case-sensitive partition. Requires partition support.</summary>
    /// <remarks>Uses Document.PartitionKey directly. The same DocumentId can exist in different partitions.
    /// UnloadContainer releases every cached partition handle for the named container. Currently supported by Cosmos, MongoDB, and CouchDB.</remarks>
    ValueTask<ILibrarianContainer> GetContainer(string containerName, string partitionKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This provider does not support explicit partitions.");

    /// <summary>Builds a read-only query across all partitions of one logical container in this database.</summary>
    /// <remarks>Currently supported by Cosmos and MongoDB. Does not include other database keys or container names.</remarks>
    ValueTask<IQueryable<T>> BuildQueryableAcrossPartitions<T>(string containerName, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This provider does not support cross-partition queries.");

    /// <summary>Executes an atomic batch within one explicit partition. Every condition and write targets that partition.</summary>
    /// <remarks>Cross-partition atomic batches are not implied by this API.</remarks>
    ValueTask<bool> Execute(LibrarianBatch batch, string partitionKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This provider does not support partition-scoped batches.");

    /// <summary>Atomically checks document conditions and applies a batch within the provider transaction boundary.</summary>
    /// <returns>True if committed; false when a condition fails. Failures and cancellation throw.</returns>
    /// <remarks>CouchDB rejects atomic batches because its bulk API is non-atomic. Existing third-party providers may also throw NotSupportedException.
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
    /// <remarks>MongoDB and Cosmos use native transactions with ExpectedVersion or CreateOnly on individual writes;
    /// raw-value conditions are unsupported. Include an unchanged versioned write when a decision depends on another document.
    /// Version conflicts, duplicate creates, or missing deletes return false and roll back the entire native batch.
    /// MongoDB batches span collections and require a replica set or sharded cluster. Confirmed pre-commit conflicts return false.
    /// Cosmos uses TransactionalBatch: at most 100 writes in one container and one Document.PartitionKey. SDK size limits apply.
    /// Neither provider automatically replays a batch. Transport failures or cancellation can leave the commit outcome unknown.
    /// Other providers reject ExpectedVersion and CreateOnly instead of silently ignoring them.
    /// MongoDB writes outside Librarian must preserve its identity and revision metadata to maintain version checks.</remarks>
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
    /// <remarks>MongoDB and Cosmos unscoped handles query all Document partitions. Point operations use Document.Id.</remarks>
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
    /// Native collection/container names are prefix.key.name, with URI-escaped segments (including escaped dots).
    /// CollectionName/ContainerName configures the prefix. Existing envelope-format data requires migration.
    /// A supplied Cosmos Container must be accessed by its physical ID and is already provisioned. Shared clients remain utility-owned.
    /// Both offer default/keyed singleton and scoped DI registrations; prefer singleton clients for connection reuse.
    /// Caller-supplied MongoDB databases and Cosmos containers retain ownership of their clients after provider disposal.
    /// The MongoDB and Cosmos SDK packages are not advertised as Native AOT compatible.
    /// CouchDB uses Librarian:CouchDb:Endpoint, optional Username and Password, Key (default librarian), DatabasePrefix
    /// (default librarian), and EnsureDatabaseOnFirstUse (default true). Constructors and DI resolution perform no I/O.
    /// Each logical container maps to one unpartitioned physical CouchDB database: prefix-hex(UTF8(key))-hex(UTF8(name)).
    /// Names and keys are case-sensitive. The encoded name must fit CouchDB's 238-byte limit.
    /// First GetContainer checks existence and creates a missing database; failures are not cached and can be retried.
    /// CouchDB has no separate container resource. Explicit Librarian partitions filter Document.PartitionKey in that database.
    /// Save and MarkDirty perform no I/O; writes commit immediately. Unload releases local handles and rechecks existence on reload.
    /// A supplied HttpClient remains caller-owned. CouchDB 3.5 or later is required for strict Mango index selection.
    /// </remarks>
    /// <remarks>MAUI Secure, LocalStorage, SessionStorage and IndexedDb buffer ordinary writes until Save, UnloadContainer or disposal.
    /// Save explicitly before suspension/navigation; process termination or browser disconnection cannot guarantee a final flush.
    /// Browser providers detect external snapshot changes and require discarding/reopening stale owners; they do not live-sync cached reads.
    /// IndexedDb uses a transaction per snapshot. LocalStorage and SessionStorage require Web Locks and cooperating writers.
    /// MAUI Secure authenticates encrypted files against an application/account scope and stores keys in platform secure storage.</remarks>
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
