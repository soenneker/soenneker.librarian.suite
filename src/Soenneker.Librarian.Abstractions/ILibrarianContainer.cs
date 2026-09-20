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
/// Raw JSON operations do not require document metadata.
/// Mutation methods check cancellation before changing data. After a change commits in memory, dirty tracking is completed
/// without cancellation for memory/filesystem providers. Redis mutations commit directly to the server, atomically updating persistent indexes.
/// Redis reads and supported queries execute against current server data; Save and MarkDirty are no-ops. The database owns container
/// lifetime; use its UnloadContainer method to save and release a container, and stop concurrent operations before unloading.
/// PostgreSQL mutations commit documents and persistent scalar indexes together in server transactions. Reads and queries use
/// current server state; PostgreSQL Save and MarkDirty are no-ops, and unloading releases only the local handle.
/// </remarks>
public interface ILibrarianContainer : IDisposable
{
    /// <summary>Builds an equality and ordered index on a case-sensitive, dot-separated JSON property path.</summary>
    /// <remarks>
    /// Idempotent per container instance. Index creation scans existing JSON once without materializing document objects.
    /// Missing properties are excluded; explicit null is indexed. Values must be strings, decimal-compatible numbers,
    /// booleans, or null. Strings use ordinal comparison; numeric comparison uses decimal semantics.
    /// Malformed JSON and non-scalar indexed values reject index creation or subsequent writes before data changes.
    /// Indexes are maintained on writes. Memory/filesystem indexes must be recreated after unload or restart; Redis and PostgreSQL indexes persist.
    /// </remarks>
    ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default);

    /// <summary>Uses an existing index to return an equality page ordered by case-insensitive document ID.</summary>
    /// <remarks>
    /// Only returned documents are deserialized. Missing indexes throw instead of silently scanning.
    /// Skip must be nonnegative and take positive. Query values follow the index's JSON scalar comparison rules.
    /// Deserialization failures propagate. The returned JSON is captured consistently with the index under an AsyncLock or, for Redis, by retrying reads when the container version changes.
    /// PostgreSQL reads the index and documents in one SQL statement snapshot.
    /// </remarks>
    ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100,
        CancellationToken cancellationToken = default);

    /// <summary>Counts equality matches directly from an existing index without deserializing documents.</summary>
    ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default);

    /// <summary>Checks an existing index for an equality match without deserializing documents.</summary>
    ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default);

    /// <summary>Returns an inclusive range ordered by indexed value, then case-insensitive document ID.</summary>
    /// <remarks>
    /// Null bounds are unbounded; two null bounds enumerate the whole index. Descending reverses both value and ID order.
    /// Values sort as null, boolean, number, then ordinal string. Missing properties are excluded. Bounds must have the
    /// same JSON scalar type when both are specified. Skip uses index rank where supported; PostgreSQL uses SQL OFFSET.
    /// Only the requested page is deserialized.
    /// This method requires an existing index and never falls back to a document scan.
    /// </remarks>
    ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default);

    /// <summary>Counts an inclusive range directly from an existing index without fetching documents.</summary>
    /// <remarks>Null bounds are unbounded. Bound types must match and minimum must not exceed maximum.
    /// Built-in providers use index cardinality; the default implementation materializes the matching JSON values.</remarks>
    async ValueTask<int> CountRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null,
        CancellationToken cancellationToken = default) =>
        (await FindRangeByIndex<System.Text.Json.JsonElement>(fieldPath, minimum, maximum, take: int.MaxValue,
            cancellationToken: cancellationToken).ConfigureAwait(false)).Items.Count;

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
    /// <remarks>All built-in providers support the async terminal extensions in LibrarianQueryableExtensions.
    /// Remote providers await I/O; local providers run cancellable CPU work without Task.Run.</remarks>
    IQueryable<T> BuildQueryable<T>();

    /// <summary>
    /// Adds a document, throwing InvalidOperationException when its ID already exists.
    /// </summary>
    /// <param name="id">The case-insensitive document ID.</param>
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
    /// use batch conditions when coordinating decisions across reads. Do not modify the IDs until the operation completes.</remarks>
    async ValueTask<string?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new string?[ids.Count];
        for (int i = 0; i < ids.Count; i++) ArgumentNullException.ThrowIfNull(ids[i]);
        for (int i = 0; i < ids.Count; i++) result[i] = await GetItem(ids[i], cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Counts all documents without deserialization or requiring a user-created index.</summary>
    /// <remarks>Built-in providers use native container cardinality. The default implementation reads document IDs.</remarks>
    async ValueTask<int> CountItems(CancellationToken cancellationToken = default) =>
        (await GetAllIds(cancellationToken).ConfigureAwait(false)).Count;

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
    ValueTask DeleteAllItems(CancellationToken cancellationToken = default);
}
