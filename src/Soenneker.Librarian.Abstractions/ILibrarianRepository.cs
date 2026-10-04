using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Documents.Document;
using Soenneker.Dtos.IdNamePair;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Abstractions;

/// <summary>
/// A data persistence abstraction layer for Librarian DB
/// </summary>
/// <typeparam name="TDocument">The type of document being handled.</typeparam>
public interface ILibrarianRepository<TDocument> where TDocument : Document
{
    /// <summary>Reads a document and its provider-neutral version, or null when absent.</summary>
    ValueTask<LibrarianItem<TDocument>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not support versioned reads.");

    /// <summary>Replaces a document only if the version matches, returning its new version or null on conflict or absence.</summary>
    ValueTask<LibrarianItem<TDocument>?> UpdateItemIfVersion(TDocument document, string version,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not support versioned writes.");

    /// <summary>Deletes a document only if its version matches. Returns false on conflict or absence.</summary>
    ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not support versioned deletes.");

    /// <summary>Retries a read-modify-write operation on confirmed conflicts. The callback must be free of external side effects and preserve the ID.</summary>
    ValueTask<LibrarianItem<TDocument>> MutateItem(string id, Func<TDocument, TDocument> mutation, int maxAttempts = 5,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This repository does not support versioned mutations.");

    /// <summary>Reads a server page with an opaque continuation token using the query's provider.</summary>
    ValueTask<LibrarianPage<T>> GetItemsPaged<T>(IQueryable<T> query, int pageSize = 100,
        string? continuationToken = null, CancellationToken cancellationToken = default) =>
        query.ToPageAsync(pageSize, continuationToken, cancellationToken);

    /// <summary>Ensures a scalar JSON field index exists on this repository's container.</summary>
    /// <remarks>Uses serialized JSON property names. See ILibrarianContainer.EnsureIndex for value and lifetime rules.</remarks>
    ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default);

    /// <summary>Returns a page of equality matches using an existing index, deserializing only returned documents.</summary>
    ValueTask<LibrarianQueryResult<TDocument>> FindByIndex(string fieldPath, object? value, int skip = 0,
        int take = 100, CancellationToken cancellationToken = default);

    /// <summary>Counts indexed equality matches without deserializing documents.</summary>
    ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default);

    /// <summary>Checks for an indexed equality match without deserializing documents.</summary>
    ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default);

    /// <summary>Returns an inclusive range page ordered by indexed value and document ID.</summary>
    /// <remarks>Null bounds are unbounded. See ILibrarianContainer.FindRangeByIndex for comparison and paging rules.</remarks>
    ValueTask<LibrarianQueryResult<TDocument>> FindRangeByIndex(string fieldPath, object? minimum = null,
        object? maximum = null, bool descending = false, int skip = 0, int take = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Obtains a deferred queryable collection of a specified type. Use LibrarianQueryableExtensions for asynchronous execution.
    /// </summary>
    /// <typeparam name="T">The type of elements in the queryable collection.</typeparam>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>A queryable collection of type <typeparamref name="T"/>.</returns>
    ValueTask<IQueryable<T>> BuildQueryable<T>(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an item by its ID.
    /// </summary>
    /// <param name="id">The unique identifier of the item.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The requested document or null if not found.</returns>
    ValueTask<TDocument?> GetItem(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously materializes a query using its provider; remote queries may block on I/O.
    /// </summary>
    /// <typeparam name="T">Type of value handled by the Librarian Repository.</typeparam>
    /// <param name="queryable">Queryable for the get items operation.</param>
    /// <returns>The requested collection.</returns>
    List<T> GetItems<T>(IQueryable<T> queryable);

    /// <summary>
    /// Retrieves all documents, returning null when the container is empty.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the collection returned by get All.</returns>
    ValueTask<List<TDocument>?> GetAll(CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an item using an IdNamePair.
    /// </summary>
    /// <param name="idNamePair">The IdNamePair containing the item's ID.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The requested document or null if not found.</returns>
    ValueTask<TDocument?> GetItemByIdNamePair(IdNamePair idNamePair, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new document to the repository.
    /// </summary>
    /// <param name="document">The document to add.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The ID of the added document.</returns>
    ValueTask<string> AddItem(TDocument document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds multiple documents to the repository.
    /// </summary>
    /// <param name="documents">The list of documents to add.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The list of added documents.</returns>
    ValueTask<List<TDocument>> AddItems(List<TDocument> documents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing document in the repository.
    /// </summary>
    /// <param name="document">The document to update.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The ID of the updated document.</returns>
    /// <exception cref="KeyNotFoundException">The document ID does not exist.</exception>
    ValueTask<string> UpdateItem(TDocument document, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates multiple documents in the repository.
    /// </summary>
    /// <param name="documents">The list of documents to update.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>The list of updated documents.</returns>
    /// <exception cref="KeyNotFoundException">A document ID does not exist.</exception>
    /// <remarks>Updates are sequential, not transactional. Earlier updates remain applied if a later update fails or is cancelled.</remarks>
    ValueTask<List<TDocument>> UpdateItems(List<TDocument> documents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a document from the repository by its ID.
    /// </summary>
    /// <param name="id">Identifier of the Librarian Repository instance or registration to target.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>An operation that completes after the documents are removed from the container.</returns>
    ValueTask DeleteItem(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all documents from the repository.
    /// </summary>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>An operation that completes after the documents are removed from the container.</returns>
    ValueTask DeleteAll(CancellationToken cancellationToken = default);
}