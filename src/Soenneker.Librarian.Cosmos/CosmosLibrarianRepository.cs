using System.Collections.Generic;
using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Cosmos.Repository.Abstract;
using Soenneker.Cosmos.Repository.Dtos;
using Soenneker.Documents.Document;
using Soenneker.Documents.Audit;
using Soenneker.Dtos.IdPartitionPair;
using Soenneker.Dtos.IdNamePair;
using Soenneker.Enums.CrudEventTypes;
using Soenneker.Constants.Data;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Cosmos;
using Soenneker.Utils.UserContext.Abstract;
using Soenneker.Utils.BackgroundQueue.Abstract;
using Soenneker.Utils.MemoryStream.Abstract;

namespace Soenneker.Librarian.Cosmos;

public abstract class CosmosLibrarianRepository<TDocument> : ICosmosRepository<TDocument> where TDocument : Document
{
    private readonly ICosmosLibrarianDatabaseFactory _database;
    private readonly IUserContext _userContext;
    protected ILogger Logger { get; }
    public abstract string ContainerName { get; }
    public virtual bool AuditEnabled => true;
    public virtual CosmosReadOptions? DefaultReadOptions => null;
    public virtual CosmosWriteOptions? DefaultWriteOptions => null;
    protected virtual bool HasScope => false;
    protected virtual bool InScope(TDocument document) => true;
    protected virtual IQueryable<TDocument> Scope(IQueryable<TDocument> query) => query;

    protected CosmosLibrarianRepository(ICosmosLibrarianDatabaseFactory database, IConfiguration config, ILogger logger,
        IUserContext userContext, IBackgroundQueue backgroundQueue, IMemoryStreamUtil memoryStreamUtil)
    {
        _database = database;
        _userContext = userContext;
        Logger = logger;
    }

    private ValueTask<ICosmosLibrarianContainer> Store(CancellationToken token, CosmosReadOptions? read = null, QueryRequestOptions? query = null) =>
        _database.Open(ContainerName, read ?? DefaultReadOptions, query, token);
    private static string Identity(string id, string partition) => id == partition ? id : partition + ":" + id;
    private async ValueTask<IQueryable<TDocument>> PartitionQuery(string partition, CosmosReadOptions? read, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        CosmosReadOptions? effective = read ?? DefaultReadOptions;
        return await BuildQueryable(new QueryRequestOptions
        {
            PartitionKey = new PartitionKey(partition),
            ReadConsistencyStrategy = effective?.ReadConsistencyStrategy,
            SessionToken = effective?.SessionToken
        }, cancellationToken: token);
    }

    private async ValueTask<List<TDocument>> ReadMany(string[] ids, CosmosReadOptions? read, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (ids.Length == 0) return [];
        ICosmosLibrarianContainer store = await Store(token, read);
        TDocument?[] documents = await store.ReadDocuments<TDocument>(ids, token);
        var result = new List<TDocument>(documents.Length);
        foreach (TDocument? document in documents)
        {
            if (document is null) continue;
            if (InScope(document)) result.Add(document);
        }
        return result;
    }
    private static CosmosException Conflict() => new("Document version changed.", HttpStatusCode.PreconditionFailed, 0, string.Empty, 0);
    private static CosmosException Missing() => new("Document not found.", HttpStatusCode.NotFound, 0, string.Empty, 0);
    private void CheckWrite(CosmosWriteOptions? options)
    {
        if (DefaultWriteOptions?.RequireETag == true || options?.RequireETag == true)
            throw new InvalidOperationException("An original ETag is required for this write.");
        if (typeof(TDocument) == typeof(AuditDocument)) throw new InvalidOperationException("Audit records are immutable.");
    }
    private static void CheckQueue(bool useQueue)
    {
        if (useQueue) throw new NotSupportedException("Repository writes must be awaited. Queue the owning application operation instead.");
    }
    private void Validate(TDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
        if (!InScope(document)) throw new InvalidOperationException("Document does not belong to this repository.");
    }
    private static async ValueTask Pause(double? milliseconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (milliseconds > 0) await Task.Delay(TimeSpan.FromMilliseconds(milliseconds.Value), token);
    }
    private static async ValueTask Parallel<T>(IEnumerable<T> values, int concurrency, Func<T, CancellationToken, ValueTask> action, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        await System.Threading.Tasks.Parallel.ForEachAsync(values, new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = token }, action);
    }
    private async ValueTask<List<T>> Sql<T>(QueryDefinition query, CosmosReadOptions? read, CancellationToken token,
        double? delayMs = null, QueryRequestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ICosmosLibrarianContainer store = await Store(token, read, options);
        return await store.QueryItems<T>(query, delayMs, token);
    }
    private async ValueTask<CosmosItem<TDocument>?> Read(string id, CosmosReadOptions? read, CancellationToken token)
    {
        ICosmosLibrarianContainer store = await Store(token, read);
        LibrarianItem<TDocument>? item = await store.ReadDocument<TDocument>(id, token);
        if (item is null) return null;
        TDocument document = item.Document;
        if (!InScope(document)) return null;
        return new CosmosItem<TDocument>(document, item.Version);
    }
    private async ValueTask<CosmosItem<TDocument>> Replace(string id, TDocument document, string? version, CancellationToken token, bool scopeVerified = false)
    {
        Validate(document);
        if (id != document.Id) throw new ArgumentException("Replacement identity must match the document.");
        if (typeof(TDocument) == typeof(AuditDocument)) throw new InvalidOperationException("Audit records are immutable.");
        if (HasScope && !scopeVerified)
        {
            CosmosItem<TDocument>? current = await Read(id, null, token);
            if (current is null) throw Missing();
            if (version is not null && current.ETag != version) throw Conflict();
            version ??= current.ETag;
        }
        ICosmosLibrarianContainer store = await Store(token);
        LibrarianItem<TDocument>? updated = await store.ReplaceDocument(document, version, token);
        if (updated is null) throw version is null ? Missing() : Conflict();
        if (AuditEnabled) await CreateAuditItem(CrudEventType.Update, id, document, CancellationToken.None);
        return new CosmosItem<TDocument>(updated.Document, updated.Version);
    }

    private async ValueTask Remove(string id, string? version, CancellationToken token)
    {
        if (typeof(TDocument) == typeof(AuditDocument)) throw new InvalidOperationException("Audit records are immutable.");
        if (HasScope)
        {
            CosmosItem<TDocument>? current = await Read(id, null, token);
            if (current is null) { if (version is not null) throw Conflict(); return; }
            if (version is not null && version != current.ETag) throw Conflict();
            version ??= current.ETag;
        }
        ICosmosLibrarianContainer store = await Store(token);
        if (version is null) await store.DeleteItem(id, token);
        else if (!await store.DeleteItemIfVersion(id, version, token)) throw Conflict();
        if (AuditEnabled) await CreateAuditItem(CrudEventType.Delete, id, cancellationToken: CancellationToken.None);
    }
    private async ValueTask<TDocument?> Mutate(string id, Func<TDocument, ValueTask<bool>> mutation, int maxAttempts, CosmosReadOptions? read, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            CosmosItem<TDocument>? current = await Read(id, read, token);
            if (current is null) return null;
            string originalId = current.Document.Id!;
            if (!await mutation(current.Document)) return current.Document;
            if (current.Document.Id != originalId) throw new ArgumentException("A mutation must preserve identity.");
            // Read already verified scope. The same ETag guards against any intervening change.
            try { return (await Replace(originalId, current.Document, current.ETag, token, scopeVerified: true)).Document; }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed && attempt + 1 < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5 * (attempt + 1)), token);
            }
        }
        throw Conflict();
    }

    public virtual async ValueTask<string> AddItem(TDocument document, bool useQueue = false, bool excludeResponse = false, CancellationToken cancellationToken = default)
    {
        CheckQueue(useQueue);
        Validate(document);
        ICosmosLibrarianContainer store = await Store(cancellationToken);
        await store.CreateDocument(document, cancellationToken);
        if (AuditEnabled) await CreateAuditItem(CrudEventType.Create, document.Id!, document, CancellationToken.None);
        return document.Id!;
    }

    public virtual async ValueTask<List<TDocument>> AddItems(List<TDocument> documents, double? delayMs = null, bool useQueue = false, bool excludeResponse = false, CancellationToken cancellationToken = default)
    {
        foreach (TDocument document in documents) { await AddItem(document, useQueue, excludeResponse, cancellationToken); await Pause(delayMs, cancellationToken); } return documents;
    }

    public virtual async ValueTask<List<TDocument>> AddItemsParallel(List<TDocument> documents, int maxConcurrency, bool excludeResponse = false, CancellationToken cancellationToken = default)
    {
        await Parallel(documents, maxConcurrency, async (document, token) => { await AddItem(document, excludeResponse: excludeResponse, cancellationToken: token); }, cancellationToken); return documents;
    }

    public virtual AuditDocument BuildDbEventAuditRecord(CrudEventType eventType, string entityId, object? entity, string? userId)
    {
        return new AuditDocument { DocumentId = Guid.NewGuid().ToString(), PartitionKey = entityId[(entityId.LastIndexOf(':') + 1)..], EntityId = entityId, EntityType = typeof(TDocument).Name, Entity = entity, EventType = eventType, UserId = userId, CreatedAt = DateTimeOffset.UtcNow };
    }

    public virtual async ValueTask CreateAuditItem(CrudEventType eventType, string entityId, object? item = null, CancellationToken cancellationToken = default)
    {
        AuditDocument audit = BuildDbEventAuditRecord(eventType, entityId, item, _userContext.GetIdSafe()); ICosmosLibrarianContainer store = await _database.Open("audits", cancellationToken: cancellationToken); await store.CreateDocument(audit, cancellationToken);
    }

    public virtual async ValueTask CreateAuditItem(CrudEventType eventType, string entityId, string entityJson, CancellationToken cancellationToken = default)
    {
        using JsonDocument parsed = JsonDocument.Parse(entityJson); await CreateAuditItem(eventType, entityId, parsed.RootElement.Clone(), cancellationToken);
    }

    public virtual async ValueTask DeleteItemIfMatch(CosmosItem<TDocument> item, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item.ETag); await Remove(item.Document.Id!, item.ETag, cancellationToken);
    }

    public virtual async ValueTask DeleteItem(string entityId, bool useQueue = false, CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckQueue(useQueue); CheckWrite(writeOptions); await Remove(entityId, null, cancellationToken);
    }

    public virtual async ValueTask DeleteItemIfMatch(string entityId, string expectedETag, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag); await Remove(entityId, expectedETag, cancellationToken);
    }

    public virtual async ValueTask DeleteItem(string documentId, string partitionKey, bool useQueue = false,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckQueue(useQueue); CheckWrite(writeOptions); await Remove(Identity(documentId, partitionKey), null, cancellationToken);
    }

    public virtual async ValueTask DeleteItemIfMatch(string documentId, string partitionKey, string expectedETag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag); await Remove(Identity(documentId, partitionKey), expectedETag, cancellationToken);
    }

    public virtual async ValueTask DeleteAll(double? delayMs = null, bool useQueue = false, CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckQueue(useQueue);
        CheckWrite(writeOptions);
        await DeleteIds(await GetAllIds(cancellationToken: cancellationToken), delayMs, useQueue, writeOptions, cancellationToken);
    }

    public virtual async ValueTask DeleteItems(IQueryable<TDocument> query, double? delayMs = null, bool useQueue = false,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckQueue(useQueue);
        CheckWrite(writeOptions);
        await DeleteIds(await GetIds(query, cancellationToken: cancellationToken), delayMs, useQueue, writeOptions, cancellationToken);
    }

    public virtual async ValueTask DeleteItemsParallel(IQueryable<TDocument> query, int maxConcurrency, double? delayMs = null,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckWrite(writeOptions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        List<IdPartitionPair> ids = await GetIds(query, cancellationToken: cancellationToken);
        if (delayMs is null or <= 0)
            await DeleteIdsParallel(ids, maxConcurrency, writeOptions, cancellationToken);
        else
            await Parallel(ids, maxConcurrency, async (pair, token) =>
            {
                await DeleteItem(pair.Id, pair.PartitionKey, false, writeOptions, token);
                await Pause(delayMs, token);
            }, cancellationToken);
    }

    public virtual async ValueTask DeleteIds(List<IdPartitionPair> ids, double? delayMs = null, bool useQueue = false,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        foreach (var pair in ids) { await DeleteItem(pair.Id, pair.PartitionKey, useQueue, writeOptions, cancellationToken); await Pause(delayMs, cancellationToken); }
    }

    public virtual async ValueTask DeleteIdsIfMatch(List<IdPartitionPair> ids, IReadOnlyDictionary<string, string> expectedETags, double? delayMs = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var pair in ids) { await DeleteItemIfMatch(pair.Id, pair.PartitionKey, expectedETags[Identity(pair.Id, pair.PartitionKey)], cancellationToken); await Pause(delayMs, cancellationToken); }
    }

    public virtual async ValueTask DeleteIdsParallel(List<IdPartitionPair> ids, int maxConcurrency,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        await Parallel(ids, maxConcurrency, async (pair, token) => { await DeleteItem(pair.Id, pair.PartitionKey, false, writeOptions, token); }, cancellationToken);
    }

    public virtual async ValueTask DeleteIdsParallelIfMatch(List<IdPartitionPair> ids, IReadOnlyDictionary<string, string> expectedETags, int maxConcurrency,
        CancellationToken cancellationToken = default)
    {
        await Parallel(ids, maxConcurrency, async (pair, token) => { await DeleteItemIfMatch(pair.Id, pair.PartitionKey, expectedETags[Identity(pair.Id, pair.PartitionKey)], token); }, cancellationToken);
    }

    public virtual async ValueTask DeleteCreatedAtBetween(DateTimeOffset startAt, DateTimeOffset endAt,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        await DeleteItems((await BuildQueryable(cancellationToken: cancellationToken)).Where(document => document.CreatedAt >= startAt && document.CreatedAt <= endAt), writeOptions: writeOptions, cancellationToken: cancellationToken);
    }

    public virtual async ValueTask<bool> Exists(string id, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        if (HasScope) return await GetItem(id, readOptions, cancellationToken) is not null;
        return await (await Store(cancellationToken, readOptions)).Exists(id, cancellationToken);
    }

    public virtual ValueTask<bool> Exists(string documentId, string partitionKey, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return Exists(Identity(documentId, partitionKey), readOptions, cancellationToken);
    }

    public virtual ValueTask<bool> Exists(IQueryable<TDocument> query, CancellationToken cancellationToken = default)
    {
        return LibrarianQueryableExtensions.Any(query, cancellationToken);
    }

    public virtual async ValueTask<bool> ExistsByPartitionKey(string partitionKey, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await Exists(await PartitionQuery(partitionKey, readOptions, cancellationToken), cancellationToken);
    }

    public virtual ValueTask<CosmosItem<TDocument>?> GetItemWithETag(string id, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return Read(id, readOptions, cancellationToken);
    }

    public virtual ValueTask<CosmosItem<TDocument>?> GetItemWithETag(string documentId, string partitionKey,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return Read(Identity(documentId, partitionKey), readOptions, cancellationToken);
    }

    public virtual async ValueTask<TDocument?> GetItem(string id, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return (await Read(id, readOptions, cancellationToken))?.Document;
    }

    public virtual async ValueTask<TDocument?> GetItemByPartitionKey(string partitionKey, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await GetItem(await PartitionQuery(partitionKey, readOptions, cancellationToken), cancellationToken);
    }

    public virtual async ValueTask<TDocument?> GetLatestByPartitionKey(string partitionKey, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await GetItem((await PartitionQuery(partitionKey, readOptions, cancellationToken)).OrderByDescending(document => document.CreatedAt), cancellationToken);
    }

    public virtual ValueTask<TDocument?> GetItemByIdNamePair(IdNamePair idNamePair, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return GetItem(idNamePair.Id, readOptions, cancellationToken);
    }

    public virtual async ValueTask<TDocument?> GetItem(string documentId, string partitionKey, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return (await Read(Identity(documentId, partitionKey), readOptions, cancellationToken))?.Document;
    }

    public virtual async ValueTask<TDocument?> GetFirst(CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await GetItem((await BuildQueryable(readOptions: readOptions, cancellationToken: cancellationToken)).OrderBy(document => document.CreatedAt), cancellationToken);
    }

    public virtual async ValueTask<TDocument?> GetLast(CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await GetItem((await BuildQueryable(readOptions: readOptions, cancellationToken: cancellationToken)).OrderByDescending(document => document.CreatedAt), cancellationToken);
    }

    public virtual async ValueTask<List<TDocument>> GetAll(double? delayMs = null, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await GetItems(await BuildQueryable(readOptions: readOptions, cancellationToken: cancellationToken), delayMs, cancellationToken);
    }

    public virtual async ValueTask<List<TDocument>> GetAllByPartitionKey(string partitionKey, double? delayMs = null,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await GetItems(await PartitionQuery(partitionKey, readOptions, cancellationToken), delayMs, cancellationToken);
    }

    public virtual ValueTask<List<TDocument>> GetItems(string query, double? delayMs = null,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return Sql<TDocument>(new QueryDefinition(query), readOptions, cancellationToken, delayMs);
    }

    public virtual async ValueTask<List<TDocument>> GetAllByDocumentIds(List<string> ids, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        if (ids.Count == 0) return [];
        IQueryable<TDocument> query = await BuildQueryable(readOptions: readOptions, cancellationToken: cancellationToken);
        var result = new List<TDocument>(ids.Count);
        foreach (string[] batch in ids.Distinct(StringComparer.Ordinal).Chunk(50))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.AddRange(await GetItems(query.Where(document => batch.Contains(document.DocumentId!)), cancellationToken: cancellationToken));
        }
        return result;
    }

    public virtual async ValueTask<List<TDocument>> GetAllByIdPartitionPairs(List<IdPartitionPair> pairs,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        return await ReadMany(pairs.Select(pair => Identity(pair.Id, pair.PartitionKey)).ToArray(), readOptions, cancellationToken);
    }

    public virtual async ValueTask<List<TDocument>> GetAllByIdNamePairs(List<IdNamePair> pairs,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        return await ReadMany(pairs.Select(pair => pair.Id).ToArray(), readOptions, cancellationToken);
    }

    public virtual ValueTask<List<T>> GetItems<T>(string query, double? delayMs = null, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return Sql<T>(new QueryDefinition(query), readOptions, cancellationToken, delayMs);
    }

    public virtual ValueTask<List<TDocument>> GetItems(QueryDefinition queryDefinition, double? delayMs = null,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return Sql<TDocument>(queryDefinition, readOptions, cancellationToken, delayMs);
    }

    public virtual ValueTask<List<T>> GetItems<T>(QueryDefinition queryDefinition, double? delayMs = null,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return Sql<T>(queryDefinition, readOptions, cancellationToken, delayMs);
    }

    public virtual async ValueTask<List<TDocument>> GetItemsBetween(DateTimeOffset startAt, DateTimeOffset endAt, double? delayMs = null,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await GetItems((await BuildQueryable(readOptions: readOptions, cancellationToken: cancellationToken)).Where(document => document.CreatedAt >= startAt && document.CreatedAt <= endAt), delayMs, cancellationToken);
    }

    public virtual ValueTask<List<IdPartitionPair>> GetIds(QueryDefinition queryDefinition, QueryRequestOptions? options = null, double? delayMs = null,
        CancellationToken cancellationToken = default)
    {
        return Sql<IdPartitionPair>(queryDefinition, null, cancellationToken, delayMs, options);
    }

    public virtual async ValueTask<List<IdPartitionPair>> GetAllIds(double? delayMs = null, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await GetIds(await BuildQueryable(readOptions: readOptions, cancellationToken: cancellationToken), delayMs, cancellationToken);
    }

    public virtual ValueTask<List<IdPartitionPair>> GetIds(IQueryable<TDocument> query, double? delayMs = null, CancellationToken cancellationToken = default)
    {
        return GetItems(query.Select(document => new IdPartitionPair { Id = document.DocumentId!, PartitionKey = document.PartitionKey! }), delayMs, cancellationToken);
    }

    public virtual async ValueTask<List<string>> GetAllPartitionKeys(double? delayMs = null, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await GetPartitionKeys(await BuildQueryable(readOptions: readOptions, cancellationToken: cancellationToken), delayMs, cancellationToken);
    }

    public virtual ValueTask<List<string>> GetPartitionKeys(IQueryable<TDocument> query, double? delayMs = null, CancellationToken cancellationToken = default)
    {
        return GetItems(query.Select(document => document.PartitionKey!).Distinct(), delayMs, cancellationToken);
    }

    public virtual async ValueTask<IQueryable<TDocument>> BuildQueryable(QueryRequestOptions? queryRequestOptions = null,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return Scope((await Store(cancellationToken, readOptions, queryRequestOptions)).BuildQueryable<TDocument>());
    }

    public virtual async ValueTask<IQueryable<T>> BuildQueryable<T>(QueryRequestOptions? queryRequestOptions = null,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        if (typeof(T) == typeof(TDocument)) return (IQueryable<T>)Scope((await Store(cancellationToken, readOptions, queryRequestOptions)).BuildQueryable<TDocument>()); if (HasScope) throw new InvalidOperationException("Project from the scoped document query."); return (await Store(cancellationToken, readOptions, queryRequestOptions)).BuildQueryable<T>();
    }

    public virtual async ValueTask<IQueryable<TDocument>> BuildPagedQueryable(int pageSize = DataConstants.DefaultCosmosPageSize, string? continuationToken = null,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return new CosmosRepositoryQueryable<TDocument>(await BuildQueryable(readOptions: readOptions, cancellationToken: cancellationToken), pageSize, continuationToken);
    }

    public virtual async ValueTask<IQueryable<T>> BuildPagedQueryable<T>(int pageSize = DataConstants.DefaultCosmosPageSize, string? continuationToken = null,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return new CosmosRepositoryQueryable<T>(await BuildQueryable<T>(readOptions: readOptions, cancellationToken: cancellationToken), pageSize, continuationToken);
    }

    public virtual ValueTask<T?> GetItem<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        return LibrarianQueryableExtensions.FirstOrDefault(query, cancellationToken);
    }

    public virtual async ValueTask<List<T>> GetItems<T>(IQueryable<T> query, double? delayMs = null, CancellationToken cancellationToken = default)
    {
        if (delayMs is null && query is not CosmosRepositoryQueryable<T>)
            return await LibrarianQueryableExtensions.ToList(query, cancellationToken);
        var result = new List<T>();
        await ExecuteOnGetItemsPaged(query, async page =>
        {
            result.EnsureCapacity(result.Count + page.Count);
            result.AddRange(page);
            if (page.Count > 0) await Pause(delayMs, cancellationToken);
        }, cancellationToken);
        return result;
    }

    public virtual ValueTask<List<TDocument>> GetItems(IQueryable<TDocument> query, double? delayMs = null, CancellationToken cancellationToken = default)
    {
        return GetItems<TDocument>(query, delayMs, cancellationToken);
    }

    public virtual async ValueTask<int> Count(CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await LibrarianQueryableExtensions.Count(await BuildQueryable(readOptions: readOptions, cancellationToken: cancellationToken), cancellationToken);
    }

    public virtual ValueTask<int> Count(IQueryable<TDocument> query, CancellationToken cancellationToken = default)
    {
        return LibrarianQueryableExtensions.Count(query, cancellationToken);
    }

    public virtual async ValueTask<bool> Any(CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await LibrarianQueryableExtensions.Any(await BuildQueryable(readOptions: readOptions, cancellationToken: cancellationToken), cancellationToken);
    }

    public virtual async ValueTask<bool> None(CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return !await Any(readOptions, cancellationToken);
    }

    public virtual async ValueTask<TDocument?> MutateItem(string id, Func<TDocument, bool> mutation, int maxAttempts = 5,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return await Mutate(id, document => ValueTask.FromResult(mutation(document)), maxAttempts, readOptions, cancellationToken);
    }

    public virtual ValueTask<TDocument?> MutateItem(string id, Func<TDocument, ValueTask<bool>> mutation, int maxAttempts = 5,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return Mutate(id, mutation, maxAttempts, readOptions, cancellationToken);
    }

    public virtual async ValueTask DeleteAllPaged(int pageSize = DataConstants.DefaultCosmosPageSize, double? delayMs = null, bool useQueue = false,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckQueue(useQueue);
        CheckWrite(writeOptions);
        IQueryable<TDocument> query = await BuildPagedQueryable(pageSize, cancellationToken: cancellationToken);
        await ExecuteOnGetItemsPaged(query.OrderBy(document => document.CreatedAt)
            .Select(document => new IdPartitionPair { Id = document.DocumentId!, PartitionKey = document.PartitionKey! }),
            ids => DeleteIds(ids, delayMs, useQueue, writeOptions, cancellationToken), cancellationToken);
    }

    public virtual async ValueTask DeleteAllPagedParallel(int maxConcurrency, int pageSize = DataConstants.DefaultCosmosPageSize,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckWrite(writeOptions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        IQueryable<TDocument> query = await BuildPagedQueryable(pageSize, cancellationToken: cancellationToken);
        await ExecuteOnGetItemsPaged(query.OrderBy(document => document.CreatedAt)
            .Select(document => new IdPartitionPair { Id = document.DocumentId!, PartitionKey = document.PartitionKey! }),
            ids => DeleteIdsParallel(ids, maxConcurrency, writeOptions, cancellationToken), cancellationToken);
    }

    public virtual async ValueTask DeleteItemsPaged(QueryDefinition queryDefinition, int pageSize = DataConstants.DefaultCosmosPageSize, double? delayMs = null, bool useQueue = false,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckQueue(useQueue);
        CheckWrite(writeOptions);
        await ExecuteOnGetItemsPaged(queryDefinition, pageSize, async documents =>
        {
            foreach (TDocument document in documents)
            {
                await DeleteItem(document.DocumentId!, document.PartitionKey!, useQueue, writeOptions, cancellationToken);
                await Pause(delayMs, cancellationToken);
            }
        }, cancellationToken: cancellationToken);
    }

    public virtual async ValueTask DeleteItemsPagedParallel(QueryDefinition queryDefinition, int maxConcurrency, int pageSize = DataConstants.DefaultCosmosPageSize,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckWrite(writeOptions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        await ExecuteOnGetItemsPaged(queryDefinition, pageSize, documents =>
            Parallel(documents, maxConcurrency, (document, token) =>
                DeleteItem(document.DocumentId!, document.PartitionKey!, writeOptions: writeOptions, cancellationToken: token), cancellationToken),
            cancellationToken: cancellationToken);
    }

    public virtual async ValueTask ExecuteOnGetItemsPaged(IQueryable<TDocument> query, Func<List<TDocument>, ValueTask> resultTask, CancellationToken cancellationToken = default)
    {
        await ExecuteOnGetItemsPaged<TDocument>(query, resultTask, cancellationToken);
    }

    public virtual async ValueTask ExecuteOnGetItemsPaged<T>(IQueryable<T> query, Func<List<T>, ValueTask> resultTask, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(resultTask);
        var defaults = query as CosmosRepositoryQueryable<T>;
        if (query.Provider is ICosmosPageReader reader)
        {
            await reader.ReadPages(query.Expression, defaults?.PageSize ?? DataConstants.DefaultCosmosPageSize,
                defaults?.ContinuationToken, resultTask, cancellationToken);
            return;
        }
        string? next = defaults?.ContinuationToken;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibrarianPage<T> page = await query.ToPage(defaults?.PageSize ?? DataConstants.DefaultCosmosPageSize, next, cancellationToken);
            await resultTask(page.Items as List<T> ?? page.Items.ToList());
            next = page.ContinuationToken;
        } while (next is not null);
    }

    public virtual async ValueTask ExecuteOnGetAllPaged(int pageSize, Func<List<TDocument>, ValueTask> resultTask,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        await ExecuteOnGetItemsPaged(await BuildPagedQueryable(pageSize, readOptions: readOptions, cancellationToken: cancellationToken), resultTask, cancellationToken);
    }

    public virtual async ValueTask ExecuteOnGetItemsPaged(QueryDefinition queryDefinition, int pageSize, Func<List<TDocument>, ValueTask> resultTask,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryDefinition);
        ArgumentNullException.ThrowIfNull(resultTask);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        ICosmosLibrarianContainer store = await Store(cancellationToken, readOptions);
        await store.QueryPages(queryDefinition, pageSize, resultTask, cancellationToken);
    }

    public virtual async ValueTask<(List<TDocument> items, string? continuationToken)> GetAllPaged(int pageSize = DataConstants.DefaultCosmosPageSize, string? continuationToken = null,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        return await GetItemsPaged(await BuildQueryable(readOptions: readOptions, cancellationToken: cancellationToken), pageSize, continuationToken, readOptions, cancellationToken);
    }

    public virtual async ValueTask<(List<TDocument> items, string? continuationToken)> GetItemsPaged(QueryDefinition queryDefinition, int pageSize, string? continuationToken,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        LibrarianPage<TDocument> page = await (await Store(cancellationToken, readOptions)).QueryPage<TDocument>(queryDefinition, pageSize, continuationToken, cancellationToken); return (page.Items as List<TDocument> ?? page.Items.ToList(), page.ContinuationToken);
    }

    public virtual async ValueTask<(List<T> items, string? continuationToken)> GetItemsPaged<T>(IQueryable<T> query, CancellationToken cancellationToken = default)
    {
        var defaults = query as CosmosRepositoryQueryable<T>; LibrarianPage<T> page = await query.ToPage(defaults?.PageSize ?? DataConstants.DefaultCosmosPageSize, defaults?.ContinuationToken, cancellationToken); return (page.Items as List<T> ?? page.Items.ToList(), page.ContinuationToken);
    }

    public virtual async ValueTask<(List<TDocument> items, string? continuationToken)> GetItemsPaged(IQueryable<TDocument> query, int pageSize, string? continuation,
        CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default)
    {
        LibrarianPage<TDocument> page = await query.ToPage(pageSize, continuation, cancellationToken); return (page.Items as List<TDocument> ?? page.Items.ToList(), page.ContinuationToken);
    }

    public virtual async ValueTask<CosmosItem<TDocument>> PatchItemIfMatch(CosmosItem<TDocument> item, List<PatchOperation> operations,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item.ETag);
        return await Patch(item.Document.Id!, operations, item.ETag, cancellationToken) ?? throw Conflict();
    }

    public virtual async ValueTask<List<TDocument>> PatchItems(List<TDocument> documents, List<PatchOperation> operations, double? delayMs = null, bool useQueue = false,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckQueue(useQueue);
        CheckWrite(writeOptions);
        for (int i = 0; i < documents.Count; i++)
        {
            documents[i] = await PatchItem(documents[i].Id!, operations, useQueue, writeOptions, cancellationToken) ?? documents[i];
            await Pause(delayMs, cancellationToken);
        }
        return documents;
    }

    public virtual async ValueTask<List<CosmosItem<TDocument>>> PatchItemsIfMatch(List<CosmosItem<TDocument>> items, List<PatchOperation> operations,
        double? delayMs = null, CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < items.Count; i++)
        {
            items[i] = await PatchItemIfMatch(items[i], operations, cancellationToken);
            await Pause(delayMs, cancellationToken);
        }
        return items;
    }

    public virtual async ValueTask<TDocument?> PatchItem(string id, List<PatchOperation> operations, bool useQueue = false,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckQueue(useQueue); CheckWrite(writeOptions); return (await Patch(id, operations, null, cancellationToken))?.Document;
    }

    public virtual async ValueTask<CosmosItem<TDocument>> PatchItemIfMatch(string id, List<PatchOperation> operations, string expectedETag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag);
        return await Patch(id, operations, expectedETag, cancellationToken) ?? throw Conflict();
    }

    public virtual async ValueTask<CosmosItem<TDocument>> UpdateItemIfMatch(CosmosItem<TDocument> item, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(item.ETag); return await Replace(item.Document.Id!, item.Document, item.ETag, cancellationToken);
    }

    public virtual async ValueTask<CosmosItem<TDocument>> UpdateItemIfMatch(TDocument document, string expectedETag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag); return await Replace(document.Id!, document, expectedETag, cancellationToken);
    }

    public virtual async ValueTask<CosmosItem<TDocument>> UpdateItemIfMatch(string id, TDocument document, string expectedETag,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedETag); return await Replace(id, document, expectedETag, cancellationToken);
    }

    public virtual async ValueTask<TDocument> UpdateItem(TDocument document, bool useQueue = false, bool excludeResponse = false,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckQueue(useQueue); CheckWrite(writeOptions); return (await Replace(document.Id!, document, null, cancellationToken)).Document;
    }

    public virtual async ValueTask<TDocument> UpdateItem(string id, TDocument document, bool useQueue = false, bool excludeResponse = false,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckQueue(useQueue); CheckWrite(writeOptions); return (await Replace(id, document, null, cancellationToken)).Document;
    }

    public virtual async ValueTask<List<TDocument>> UpdateItems(List<TDocument> documents, double? delayMs = null, bool useQueue = false, bool excludeResponse = false,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckQueue(useQueue);
        CheckWrite(writeOptions);
        for (int i = 0; i < documents.Count; i++)
        {
            documents[i] = await UpdateItem(documents[i], useQueue, excludeResponse, writeOptions, cancellationToken);
            await Pause(delayMs, cancellationToken);
        }
        return documents;
    }

    public virtual async ValueTask<List<CosmosItem<TDocument>>> UpdateItemsIfMatch(List<CosmosItem<TDocument>> items, double? delayMs = null,
        CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < items.Count; i++)
        {
            items[i] = await UpdateItemIfMatch(items[i], cancellationToken);
            await Pause(delayMs, cancellationToken);
        }
        return items;
    }

    public virtual async ValueTask<List<TDocument>> UpdateItemsParallel(List<TDocument> documents, int maxConcurrency, bool excludeResponse = false,
        CosmosWriteOptions? writeOptions = null, CancellationToken cancellationToken = default)
    {
        CheckWrite(writeOptions);
        await Parallel(Enumerable.Range(0, documents.Count), maxConcurrency, async (index, token) =>
        {
            documents[index] = await UpdateItem(documents[index], excludeResponse: excludeResponse, writeOptions: writeOptions, cancellationToken: token);
        }, cancellationToken);
        return documents;
    }

    public virtual async ValueTask<List<CosmosItem<TDocument>>> UpdateItemsParallelIfMatch(List<CosmosItem<TDocument>> items, int maxConcurrency,
        CancellationToken cancellationToken = default)
    {
        foreach (CosmosItem<TDocument> item in items) ArgumentException.ThrowIfNullOrWhiteSpace(item.ETag);
        await Parallel(Enumerable.Range(0, items.Count), maxConcurrency, async (index, token) =>
        {
            items[index] = await UpdateItemIfMatch(items[index], token);
        }, cancellationToken);
        return items;
    }

    private async ValueTask<CosmosItem<TDocument>?> Patch(string id, List<PatchOperation> operations, string? version, CancellationToken token)
    {
        if (typeof(TDocument) == typeof(AuditDocument)) throw new InvalidOperationException("Audit records are immutable.");
        if (HasScope)
        {
            CosmosItem<TDocument>? current = await Read(id, null, token);
            if (current is null) return null;
            if (version is not null && version != current.ETag) return null;
            version ??= current.ETag;
            if (operations.Any(operation => operation.Path == "/entityType" || operation.Path.StartsWith("/entityType/", StringComparison.Ordinal)))
                throw new InvalidOperationException("A patch cannot change repository scope.");
        }
        ICosmosLibrarianContainer store = await Store(token);
        LibrarianItem<TDocument>? result = await store.PatchDocument<TDocument>(id, operations, version, token);
        if (result is null) return null;
        TDocument document = result.Document;
        if (AuditEnabled) await CreateAuditItem(CrudEventType.Update, id, result.Document, CancellationToken.None);
        return new CosmosItem<TDocument>(document, result.Version);
    }
}
