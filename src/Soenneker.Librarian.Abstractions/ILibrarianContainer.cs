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
/// Mutation methods check cancellation before changing data. After a change commits in memory, dirty tracking is completed
/// without cancellation for memory/filesystem providers. Redis mutations commit directly to the server, atomically updating persistent indexes.
/// Redis reads and supported queries execute against current server data; Save and MarkDirty are no-ops. The database owns container
/// lifetime; use its UnloadContainer method to save and release a container, and stop concurrent operations before unloading.
/// </remarks>
public interface ILibrarianContainer : IDisposable
{
    /// <summary>Builds an equality and ordered index on a case-sensitive, dot-separated JSON property path.</summary>
    /// <remarks>
    /// Idempotent per container instance. Index creation scans existing JSON once without materializing document objects.
    /// Missing properties are excluded; explicit null is indexed. Values must be strings, decimal-compatible numbers,
    /// booleans, or null. Strings use ordinal comparison; numeric comparison uses decimal semantics.
    /// Malformed JSON and non-scalar indexed values reject index creation or subsequent writes before data changes.
    /// Indexes are maintained on writes. Memory/filesystem indexes must be recreated after unload or restart; Redis indexes persist.
    /// </remarks>
    ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default);

    /// <summary>Uses an existing index to return an equality page ordered by case-insensitive document ID.</summary>
    /// <remarks>
    /// Only returned documents are deserialized. Missing indexes throw instead of silently scanning.
    /// Skip must be nonnegative and take positive. Query values follow the index's JSON scalar comparison rules.
    /// Deserialization failures propagate. The returned JSON is captured consistently with the index under an AsyncLock or, for Redis, by retrying reads when the container version changes.
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
    /// same JSON scalar type when both are specified. Skip seeks by index rank and deserializes only the requested page.
    /// This method requires an existing index and never falls back to a document scan.
    /// </remarks>
    ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a deferred query with automatic indexes for supported scalar property filters and ordering.
    /// </summary>
    /// <typeparam name="T">Type of value handled by the Librarian Container.</typeparam>
    /// <returns>The resulting queryable.</returns>
    /// <remarks>For memory/filesystem providers, the first indexed query builds a lookup; writes maintain it. Supported leading filters, ordering and paging deserialize only the selected page. Other expressions use ordinary LINQ over a raw JSON snapshot, deserializing documents as consumed and skipping invalid or null documents. Writes invalidate the cached raw snapshot. Enumeration executes synchronously.</remarks>
    /// <remarks>Redis supports scalar comparisons, boolean AND/OR/NOT, one ordering, paging, Count/LongCount/Any and First/Single variants.
    /// Filtering and ordering must precede paging. Unsupported Redis expressions throw NotSupportedException; no local fallback is performed.</remarks>
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
