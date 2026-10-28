using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Documents.Document;
using Soenneker.Dtos.IdNamePair;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Core;

public class LibrarianRepository<TDocument> : ILibrarianRepository<TDocument> where TDocument : Document
{
    private readonly ILibrarianDatabase _database;
    private readonly string? _partitionKey;
    private readonly ILibrarianRepositoryScope<TDocument>? _scope;
    private readonly ILibrarianRepositoryLifecycle<TDocument>? _lifecycle;
    private readonly int _maxConcurrency;

    protected ILogger<LibrarianRepository<TDocument>> Logger { get; }

    private readonly bool _log;

    protected string ContainerName { get; set; }

    public LibrarianRepository(IConfiguration config, ILogger<LibrarianRepository<TDocument>> logger, ILibrarianDatabase database, string containerName)
        : this(config, logger, database, containerName, null) { }

    public LibrarianRepository(IConfiguration config, ILogger<LibrarianRepository<TDocument>> logger, ILibrarianDatabase database, string containerName,
        string? partitionKey, ILibrarianRepositoryScope<TDocument>? scope = null,
        ILibrarianRepositoryLifecycle<TDocument>? lifecycle = null, int maxConcurrency = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        if (partitionKey is not null) ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrency);
        _scope = scope;
        _lifecycle = lifecycle;
        _maxConcurrency = maxConcurrency;
        _database = database;
        _partitionKey = partitionKey;
        ContainerName = containerName;
        Logger = logger;

        _log = bool.Parse(config["Librarian:Log"] ?? "false");
    }

    protected ValueTask<ILibrarianContainer> GetContainer(CancellationToken token) => _partitionKey is null
        ? _database.GetContainer(ContainerName, token) : _database.GetContainer(ContainerName, _partitionKey, token);

    private bool Contains(TDocument? document) => document is not null && (_scope?.Contains(document) ?? true);

    private void Validate(TDocument document, string? originalId = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
        if (originalId is not null && document.Id != originalId)
            throw new ArgumentException("Repository hooks and mutations must preserve document identity.");
        if (!Contains(document)) throw new ArgumentException("The document is outside this repository's scope.");
    }

    public async ValueTask<LibrarianItem<TDocument>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default)
    {
        LibrarianItem<TDocument>? item = await (await GetContainer(cancellationToken).NoSync()).GetItemWithVersion<TDocument>(id, cancellationToken).NoSync();
        return item is not null && Contains(item.Document) ? item : null;
    }

    public async ValueTask<LibrarianItem<TDocument>?> UpdateItemIfVersion(TDocument document, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Validate(document);
        if (_scope is not null)
        {
            LibrarianItem<TDocument>? current = await GetItemWithVersion(document.Id!, cancellationToken).NoSync();
            if (current is null || current.Version != version) return null;
        }
        string id = document.Id!;
        if (_lifecycle is not null) await _lifecycle.BeforeUpdate(document, cancellationToken).NoSync();
        Validate(document, id);
        LibrarianItem<TDocument>? result = await (await GetContainer(cancellationToken).NoSync()).UpdateItemIfVersion(id, document, version, cancellationToken).NoSync();
        if (result is not null && _lifecycle is not null) await _lifecycle.AfterUpdate(result.Document).NoSync();
        return result;
    }

    public async ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        LibrarianItem<TDocument>? current = null;
        if (_scope is not null || _lifecycle is not null)
        {
            current = await GetItemWithVersion(id, cancellationToken).NoSync();
            if (current is null || current.Version != version) return false;
            string originalId = current.Document.Id!;
            if (_lifecycle is not null) await _lifecycle.BeforeDelete(current.Document, cancellationToken).NoSync();
            Validate(current.Document, originalId);
        }
        bool deleted = await (await GetContainer(cancellationToken).NoSync()).DeleteItemIfVersion(id, version, cancellationToken).NoSync();
        if (deleted && _lifecycle is not null) await _lifecycle.AfterDelete(current!.Document).NoSync();
        return deleted;
    }

    public async ValueTask<LibrarianItem<TDocument>> MutateItem(string id, Func<TDocument, TDocument> mutation, int maxAttempts = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LibrarianItem<TDocument> current = await GetItemWithVersion(id, cancellationToken).NoSync()
                ?? throw new KeyNotFoundException($"Document '{id}' does not exist in this repository.");
            string originalId = current.Document.Id!;
            TDocument changed = mutation(current.Document);
            Validate(changed, originalId);
            LibrarianItem<TDocument>? result = await UpdateItemIfVersion(changed, current.Version, cancellationToken).NoSync();
            if (result is not null) return result;
        }
        throw new LibrarianConcurrencyException($"Document '{id}' changed during all {maxAttempts} mutation attempts.");
    }

    public ValueTask<LibrarianPage<T>> GetItemsPaged<T>(IQueryable<T> query, int pageSize = 100, string? continuationToken = null,
        CancellationToken cancellationToken = default) => query.ToPage(pageSize, continuationToken, cancellationToken);

    public async ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();
        await container.EnsureIndex(fieldPath, cancellationToken).NoSync();
    }

    public async ValueTask<LibrarianQueryResult<TDocument>> FindByIndex(string fieldPath, object? value, int skip = 0, int take = 100,
        CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();
        if (_scope is null) return await container.FindByIndex<TDocument>(fieldPath, value, skip, take, cancellationToken).NoSync();
        return await ScopedIndexPage((offset, count) => container.FindByIndex<TDocument>(fieldPath, value, offset, count, cancellationToken),
            fieldPath, skip, take, cancellationToken).NoSync();
    }

    public async ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();
        if (_scope is null) return await container.CountByIndex(fieldPath, value, cancellationToken).NoSync();
        int count = 0;
        for (int offset = 0; ; offset += 256)
        {
            LibrarianQueryResult<TDocument> page = await container.FindByIndex<TDocument>(fieldPath, value, offset, 256, cancellationToken).NoSync();
            count = checked(count + page.Items.Count(Contains));
            if (page.Items.Count < 256) return count;
        }
    }

    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();
        if (_scope is null) return await container.ExistsByIndex(fieldPath, value, cancellationToken).NoSync();
        return (await FindByIndex(fieldPath, value, 0, 1, cancellationToken).NoSync()).Items.Count != 0;
    }

    public async ValueTask<LibrarianQueryResult<TDocument>> FindRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();
        if (_scope is null) return await container.FindRangeByIndex<TDocument>(fieldPath, minimum, maximum, descending, skip, take, cancellationToken).NoSync();
        return await ScopedIndexPage((offset, count) => container.FindRangeByIndex<TDocument>(fieldPath, minimum, maximum, descending, offset, count, cancellationToken),
            fieldPath, skip, take, cancellationToken).NoSync();
    }

    // Scope before logical skip/take. Providers retain their index ordering; no unrelated row is returned.
    private async ValueTask<LibrarianQueryResult<TDocument>> ScopedIndexPage(
        Func<int, int, ValueTask<LibrarianQueryResult<TDocument>>> read, string path, int skip, int take, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        var items = new List<TDocument>();
        int examined = 0, deserialized = 0;
        LibrarianQueryResult<TDocument> Result() => new() { Index = path, Items = items,
            IndexEntriesExamined = examined, DocumentsDeserialized = deserialized };
        for (int offset = 0; ; offset += 256)
        {
            token.ThrowIfCancellationRequested();
            LibrarianQueryResult<TDocument> page = await read(offset, 256).NoSync();
            examined += page.IndexEntriesExamined;
            deserialized += page.DocumentsDeserialized;
            foreach (TDocument document in page.Items)
            {
                if (!Contains(document)) continue;
                if (skip > 0) { skip--; continue; }
                items.Add(document);
                if (items.Count == take) return Result();
            }
            if (page.Items.Count < 256) return Result();
        }
    }

    public async ValueTask<IQueryable<T>> BuildQueryable<T>(CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();

        if (_scope is null) return container.BuildQueryable<T>();
        if (typeof(T) != typeof(TDocument))
            throw new InvalidOperationException("Build a scoped document query before projecting to another type.");
        return (IQueryable<T>)_scope.Apply(container.BuildQueryable<TDocument>());
    }

    public ValueTask<List<T>> GetItems<T>(IQueryable<T> queryable, CancellationToken cancellationToken = default)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("-- LIBRARIAN: {method} ({type})", nameof(GetItems), typeof(T).Name);

        // Execute through the asynchronous provider so remote queries never block.
        return queryable.ToList(cancellationToken);
    }

    public async ValueTask<TDocument?> GetItem(string id, CancellationToken cancellationToken = default)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("-- LIBRARIAN: {method} ({type}): {id}", nameof(GetItem), typeof(TDocument).Name, id);

        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();

        string? item = await container.GetItem(id, cancellationToken).NoSync();

        if (item == null)
            return null;

        TDocument? document = LibrarianJson.Deserialize<TDocument>(item);
        return Contains(document) ? document : null;
    }

    public async ValueTask<List<TDocument>?> GetAll(CancellationToken cancellationToken = default)
    {
        List<TDocument> items = await (await BuildQueryable<TDocument>(cancellationToken).NoSync()).ToList(cancellationToken).NoSync();
        return items.Count == 0 ? null : items;
    }

    public async ValueTask<TDocument?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        string?[] rows = await (await GetContainer(cancellationToken).NoSync()).GetItems(ids, cancellationToken).NoSync();
        var result = new TDocument?[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TDocument? document = rows[i] is null ? null : LibrarianJson.Deserialize<TDocument>(rows[i]!);
            if (Contains(document)) result[i] = document;
        }
        return result;
    }

    public ValueTask<TDocument?> GetItemByIdNamePair(IdNamePair idNamePair, CancellationToken cancellationToken = default)
    {
        return GetItem(idNamePair.Id, cancellationToken);
    }

    public virtual async ValueTask<string> AddItem(TDocument document, CancellationToken cancellationToken = default)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("-- LIBRARIAN: {method} ({type})", nameof(AddItem), typeof(TDocument).Name);
        ArgumentNullException.ThrowIfNull(document);
        string? id = document.Id;
        if (_lifecycle is not null) await _lifecycle.BeforeAdd(document, cancellationToken).NoSync();
        Validate(document, id);
        await (await GetContainer(cancellationToken).NoSync()).AddItem(document.Id!, LibrarianJson.Serialize(document), cancellationToken).NoSync();
        if (_lifecycle is not null) await _lifecycle.AfterAdd(document).NoSync();
        return document.Id!;
    }

    private async ValueTask<List<TDocument>> WriteItems(List<TDocument> documents,
        Func<TDocument, CancellationToken, ValueTask<string>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();
        if (_maxConcurrency == 1)
        {
            foreach (TDocument document in documents) await write(document, cancellationToken).NoSync();
        }
        else
        {
            await Parallel.ForEachAsync(documents,
                new ParallelOptions { MaxDegreeOfParallelism = _maxConcurrency, CancellationToken = cancellationToken },
                async (document, token) => { await write(document, token).NoSync(); });
        }
        return documents;
    }

    public virtual ValueTask<List<TDocument>> AddItems(List<TDocument> documents, CancellationToken cancellationToken = default) =>
        WriteItems(documents, AddItem, cancellationToken);

    public virtual async ValueTask<string> UpdateItem(TDocument document, CancellationToken cancellationToken = default)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("-- LIBRARIAN: {method} ({type})", nameof(UpdateItem), typeof(TDocument).Name);
        Validate(document);
        if (_scope is not null)
        {
            LibrarianItem<TDocument> current = await GetItemWithVersion(document.Id!, cancellationToken).NoSync()
                ?? throw new KeyNotFoundException("The document does not exist in this repository.");
            if (await UpdateItemIfVersion(document, current.Version, cancellationToken).NoSync() is null)
                throw new LibrarianConcurrencyException("The document changed before the scoped update.");
        }
        else
        {
            string id = document.Id!;
            if (_lifecycle is not null) await _lifecycle.BeforeUpdate(document, cancellationToken).NoSync();
            Validate(document, id);
            await (await GetContainer(cancellationToken).NoSync()).UpdateItemStrict(id, LibrarianJson.Serialize(document), cancellationToken).NoSync();
            if (_lifecycle is not null) await _lifecycle.AfterUpdate(document).NoSync();
        }
        return document.Id!;
    }

    public virtual ValueTask<List<TDocument>> UpdateItems(List<TDocument> documents, CancellationToken cancellationToken = default) =>
        WriteItems(documents, UpdateItem, cancellationToken);

    public virtual async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default)
    {
        if (_scope is null && _lifecycle is null)
        {
            await (await GetContainer(cancellationToken).NoSync()).DeleteItem(id, cancellationToken).NoSync();
            return;
        }
        LibrarianItem<TDocument>? current = await GetItemWithVersion(id, cancellationToken).NoSync();
        if (current is null) return;
        if (!await DeleteItemIfVersion(id, current.Version, cancellationToken).NoSync())
            throw new LibrarianConcurrencyException("The document changed before the scoped deletion.");
    }

    public virtual async ValueTask DeleteAll(CancellationToken cancellationToken = default)
    {
        Logger.LogWarning("-- LIBRARIAN: {method} ({type})", nameof(DeleteAll), typeof(TDocument).Name);
        if (_scope is null && _lifecycle is null)
        {
            await (await GetContainer(cancellationToken).NoSync()).DeleteAllItems(cancellationToken).NoSync();
            return;
        }
        List<TDocument>? documents = await GetAll(cancellationToken).NoSync();
        if (documents is null) return;
        foreach (TDocument document in documents) await DeleteItem(document.Id!, cancellationToken).NoSync();
    }
}
