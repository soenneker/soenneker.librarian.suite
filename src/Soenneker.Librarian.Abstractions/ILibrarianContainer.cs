using Soenneker.Extensions.ValueTask;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Abstractions;

/// <summary>
/// Defines the contract for a generic container storing documents in the Librarian database.
/// </summary>
/// <remarks>
/// Typed operations require source-generated contracts registered through LibrarianJson.Register&lt;T&gt; before use.
/// Raw JSON operations do not require registered serialization contracts.
/// Mutation methods check cancellation before changing data. After a change commits in memory, dirty tracking is completed
/// without cancellation for memory/filesystem providers. Redis mutations commit directly to the server, atomically updating persistent indexes.
/// Redis reads and supported queries execute against current server data; Save and MarkDirty are no-ops. The database owns container
/// lifetime; use its UnloadContainer method to save and release a container, and stop concurrent operations before unloading.
/// PostgreSQL mutations commit documents and persistent scalar indexes together in server transactions. Reads and queries use
/// current server state; PostgreSQL Save and MarkDirty are no-ops, and unloading releases only the local handle.
/// MongoDB and Cosmos store Document JSON directly in one native collection/container per logical name.
/// Native writes require top-level id (DocumentId) and partitionKey matching the requested Document.Id.
/// Native identities are case-sensitive. An unscoped handle accepts composite Document.Id values and queries all partitions;
/// a scoped handle also accepts a bare DocumentId, and rejects a conflicting partition. No document envelopes are stored.
/// Raw reads return serialized document fields without provider metadata; original whitespace is not preserved.
/// MongoDB reserves _id and _librarianVersion for identity and optimistic concurrency; Cosmos uses its system ETag.
/// Version tokens are exposed through LibrarianItem, not the application's Document model.
/// Native numeric, string, null/missing, index, consistency, and query translation semantics apply.
/// </remarks>
public interface ILibrarianContainer : IDisposable
{
    /// <summary>Reads a document with an opaque version, or null when absent. Requires versioning support.</summary>
    /// <remarks>Currently supported by Cosmos, MongoDB, and CouchDB. Other providers throw NotSupportedException.</remarks>
    ValueTask<LibrarianItem<string>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This provider does not support versioned reads.");

    /// <summary>Replaces a document only if its current version matches. Returns the new version, or null on conflict or absence.</summary>
    /// <remarks>Never retries an uncertain commit. A transport failure after dispatch requires authoritative reconciliation.</remarks>
    ValueTask<LibrarianItem<string>?> UpdateItemIfVersion(string id, string document, string version,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This provider does not support versioned writes.");

    /// <summary>Deletes only if the version matches. Returns false on conflict or absence.</summary>
    ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This provider does not support versioned deletes.");

    /// <summary>Builds an equality and ordered index on a case-sensitive, dot-separated JSON property path.</summary>
    /// <remarks>
    /// Idempotent per container instance. Index creation scans existing JSON once without materializing document objects.
    /// Missing properties are excluded; explicit null is indexed. Values must be strings, decimal-compatible numbers,
    /// booleans, or null. Strings use ordinal comparison; numeric comparison uses decimal semantics.
    /// Malformed JSON and non-scalar indexed values reject index creation or subsequent writes before data changes.
    /// Indexes are maintained on writes. Memory/filesystem indexes must be recreated after unload or restart; Redis and PostgreSQL indexes persist.
    /// MongoDB uses native BSON indexes, including multikey indexes; it does not validate scalar fields on writes.
    /// MongoDB creates secondary indexes only when EnsureIndex is explicitly called. Reads never create indexes;
    /// without an appropriate index, MongoDB may scan or sort documents. Its default _id index enforces document identity.
    /// Cosmos uses its configured native indexing policy. EnsureIndex validates the path and performs no I/O.
    /// CouchDB creates a native Mango JSON index explicitly, including partition and identity fields for ordering.
    /// Queries require EnsureIndex first; fallback to scans is disabled. CouchDB ICU string collation and JSON type semantics
    /// apply. Counts fetch matching IDs in bookmark pages; they do not deserialize application documents.
    /// Librarian never modifies Cosmos indexing policies; configure composite indexes explicitly for multi-field LINQ ordering.
    /// </remarks>
    /// <exception cref="TimeoutException">Redis index creation exhausted its bounded conflict retries.</exception>
    ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default);

    /// <summary>Uses an existing index to return an equality page ordered by document ID (case-sensitive for native document providers).</summary>
    /// <remarks>
    /// Only returned documents are deserialized. Local providers require a registered index; native providers use their query planners.
    /// Skip must be nonnegative and take positive. Query values follow the index's JSON scalar comparison rules.
    /// Deserialization failures propagate. The returned JSON is captured consistently with the index under an AsyncLock or, for Redis, by retrying reads when the container version changes.
    /// PostgreSQL reads the index and documents in one SQL statement snapshot.
    /// Cosmos equality pages order by DocumentId only; ties across partitions have no guaranteed order.
    /// </remarks>
    /// <exception cref="TimeoutException">Redis could not obtain a consistent index snapshot within its bounded conflict retries.</exception>
    ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100,
        CancellationToken cancellationToken = default);

    /// <summary>Counts equality matches directly from an existing index without deserializing documents.</summary>
    ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default);

    /// <summary>Checks an existing index for an equality match without deserializing documents.</summary>
    ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default);

    /// <summary>Returns an inclusive range ordered by indexed value, then document identity (case-sensitive for native document providers).</summary>
    /// <remarks>
    /// Null bounds are unbounded; two null bounds enumerate the whole index. Descending reverses both value and ID order.
    /// Values sort as null, boolean, number, then ordinal string. Missing properties are excluded. Bounds must have the
    /// same JSON scalar type when both are specified. Skip uses index rank where supported; PostgreSQL uses SQL OFFSET.
    /// Only the requested page is deserialized.
    /// Local providers require an existing index. MongoDB uses its native query planner and may scan if no suitable index exists.
    /// Cosmos uses its native policy and orders only by the requested field; equal values have no guaranteed order.
    /// </remarks>
    /// <exception cref="TimeoutException">Redis could not obtain a consistent index snapshot within its bounded conflict retries.</exception>
    ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null,
        object? maximum = null, bool descending = false, int skip = 0, int take = 100,
        CancellationToken cancellationToken = default);

    /// <summary>Counts an inclusive range directly from an existing index without fetching documents.</summary>
    /// <remarks>Null bounds are unbounded. Bound types must match and minimum must not exceed maximum.
    /// Built-in providers use index cardinality; the default implementation materializes the matching JSON values.</remarks>
    async ValueTask<int> CountRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null,
        CancellationToken cancellationToken = default) =>
        (await FindRangeByIndex<System.Text.Json.JsonElement>(fieldPath, minimum, maximum, take: int.MaxValue,
            cancellationToken: cancellationToken).NoSync()).Items.Count;

    /// <summary>
    /// Creates a deferred query with automatic indexes for supported scalar property filters and ordering.
    /// </summary>
    /// <typeparam name="T">Type of value handled by the Librarian Container.</typeparam>
    /// <returns>The resulting queryable.</returns>
    /// <remarks>For memory/filesystem providers, the first indexed query builds a lookup; writes maintain it. Supported leading filters, ordering and paging deserialize only the selected page. Other supported operators use an AOT-safe local executor over a raw JSON snapshot, deserializing documents as consumed and skipping invalid or null documents. Unsupported operators throw; call AsEnumerable() for additional client-side LINQ. Writes invalidate the cached raw snapshot. Enumeration executes synchronously.</remarks>
    /// <remarks>Redis supports scalar comparisons, boolean AND/OR/NOT, ordinal StartsWith, captured membership, one ordering,
    /// paging, Count/LongCount/Any/All and First/Single variants. Direct scalar/constructor/DTO projections require a bounded page.
    /// Filtering and ordering must precede paging and projection. Unsupported Redis expressions throw NotSupportedException.</remarks>
    /// <remarks>PostgreSQL additionally supports ThenBy, ordinal StartsWith/EndsWith/Contains, captured array/list/default-comparer
    /// set membership, All, direct scalar/constructor/member-initializer Select projections, and numeric Sum/Average/Min/Max.
    /// PostgreSQL also supports nested filtering/ordering after paging or mappable projection, scalar Distinct,
    /// numeric arithmetic and field comparisons, and UTF-16 string length. Projected fields and numeric aggregates read JSONB;
    /// only selected results are fetched. Filter and ordering paths build persistent indexes on first use.
    /// Missing indexed properties are excluded from ordering; negated equality includes missing properties.
    /// PostgreSQL unsupported expressions throw NotSupportedException. SQL OFFSET may visit skipped index entries.</remarks>
    /// <remarks>All built-in providers except CouchDB support the async terminal extensions in LibrarianQueryableExtensions.
    /// Remote providers await I/O; local providers run cancellable CPU work without Task.Run.</remarks>
    /// <remarks>MongoDB delegates LINQ translation to the MongoDB driver's LINQ3 provider. Filtering, ordering,
    /// grouping, aggregates, nested arrays, and supported projections execute on the server without requiring a bounded page.
    /// Registered JSON contracts supply member names and serialization directly over top-level BSON document fields.
    /// LINQ follows native MongoDB numeric and null/missing semantics and does not register scalar indexes automatically.
    /// Client-side projections are disabled. Unsupported driver expressions and overloads throw, including Take(0),
    /// ordinal StringComparison overloads of StartsWith, and Join with a filtered inner source (such as a Librarian container).
    /// JSON date strings remain strings, so BSON date-only operations require an explicit server conversion.
    /// Both Librarian async terminals and MongoDB driver async/cursor extensions are supported.</remarks>
    /// <remarks>Cosmos delegates expression translation and materialization to the Cosmos SDK and its configured serializer,
    /// including its supported filters, ordering, projections, and aggregates. It does not require a bounded projection.
    /// Cosmos SDK LINQ does not require LibrarianJson registration. Use native
    /// Cosmos indexing policies for additional composite ordering. Unsupported expressions are rejected by the SDK.</remarks>
    /// <remarks>CouchDB rejects LINQ translation. Use explicit JSON-path index methods for reflection-free server queries.
    /// An unscoped CouchDB container queries all Document partitions; explicit handles filter one partition.</remarks>
    IQueryable<T> BuildQueryable<T>();

    /// <summary>
    /// Adds a document, throwing InvalidOperationException when its ID already exists.
    /// </summary>
    /// <param name="id">The document ID. MongoDB and Cosmos use case-sensitive Document identities.</param>
    /// <param name="document">The document to add.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The added document.</returns>
    ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a document by its ID.
    /// </summary>
    /// <param name="id">The ID of the document.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <returns>The document if found, otherwise null.</returns>
    ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default);

    /// <summary>Reads documents in request order, preserving duplicates and returning null for missing IDs.</summary>
    /// <remarks>Built-in providers capture one consistent snapshot without deserializing documents. Redis uses one server
    /// script and PostgreSQL one statement. The default implementation for third-party providers performs separate reads;
    /// MongoDB uses a native find and Cosmos uses ReadMany, with their native consistency guarantees rather than an additional snapshot protocol.
    /// use batch conditions when coordinating decisions across reads. Do not modify the IDs until the operation completes.</remarks>
    async ValueTask<string?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new string?[ids.Count];
        for (var i = 0; i < ids.Count; i++)
            ArgumentNullException.ThrowIfNull(ids[i]);
        for (var i = 0; i < ids.Count; i++)
            result[i] = await GetItem(ids[i], cancellationToken).NoSync();
        return result;
    }

    /// <summary>Counts all documents without deserialization or requiring a user-created index.</summary>
    /// <remarks>Providers normally use native cardinality. CouchDB enumerates native document rows to exclude design documents and apply partition scope. The default implementation reads document IDs.</remarks>
    async ValueTask<int> CountItems(CancellationToken cancellationToken = default) =>
        (await GetAllIds(cancellationToken).NoSync()).Count;

    /// <summary>
    /// Retrieves a document by its ID, throwing an exception if not found.
    /// </summary>
    /// <param name="id">The ID of the document.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <returns>The found document.</returns>
    ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing document, returning null if its ID is missing.
    /// </summary>
    /// <param name="id">Identifier of the Librarian Container instance or registration to target.</param>
    /// <param name="document">Document to read, persist, or update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated JSON document, or null if the ID is missing.</returns>
    ValueTask<string?> UpdateItem(string id, string document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing document, throwing KeyNotFoundException if its ID is missing.
    /// </summary>
    /// <param name="id">Identifier of the Librarian Container instance or registration to target.</param>
    /// <param name="document">Document to read, persist, or update.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The updated JSON document.</returns>
    ValueTask<string> UpdateItemStrict(string id, string document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a document from the container.
    /// </summary>
    /// <param name="id">Identifier of the Librarian Container instance or registration to target.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An operation that completes after the in-memory mutation is tracked or the Redis mutation commits.</returns>
    ValueTask DeleteItem(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all items in the container.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <returns>A list of all stored documents.</returns>
    ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a detached list of document IDs.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <returns>The requested collection.</returns>
    ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a detached list of document ID and JSON value pairs.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <returns>The requested collection.</returns>
    ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every document from the container.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>An operation that completes after the in-memory mutation is tracked or the Redis mutation commits.</returns>
    /// <remarks>MongoDB uses DeleteMany; Cosmos enumerates IDs and deletes documents individually.
    /// Native clears are not atomic. Cancellation or failure can leave a partially cleared container.</remarks>
    ValueTask DeleteAllItems(CancellationToken cancellationToken = default);
}