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

    protected ILogger<LibrarianRepository<TDocument>> Logger { get; }

    private readonly bool _log;

    protected string ContainerName { get; set; }

    public LibrarianRepository(IConfiguration config, ILogger<LibrarianRepository<TDocument>> logger, ILibrarianDatabase database, string containerName)
        : this(config, logger, database, containerName, null) { }

    public LibrarianRepository(IConfiguration config, ILogger<LibrarianRepository<TDocument>> logger, ILibrarianDatabase database, string containerName,
        string? partitionKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        if (partitionKey is not null) ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        _database = database;
        _partitionKey = partitionKey;
        ContainerName = containerName;
        Logger = logger;

        _log = bool.Parse(config["Librarian:Log"] ?? "false");
    }

    protected ValueTask<ILibrarianContainer> GetContainer(CancellationToken token) => _partitionKey is null
        ? _database.GetContainer(ContainerName, token) : _database.GetContainer(ContainerName, _partitionKey, token);

    public async ValueTask<LibrarianItem<TDocument>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default) =>
        await (await GetContainer(cancellationToken).NoSync()).GetItemWithVersion<TDocument>(id, cancellationToken).NoSync();

    public async ValueTask<LibrarianItem<TDocument>?> UpdateItemIfVersion(TDocument document, string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(document.Id);
        return await (await GetContainer(cancellationToken).NoSync()).UpdateItemIfVersion(document.Id, document, version, cancellationToken).NoSync();
    }

    public async ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default) =>
        await (await GetContainer(cancellationToken).NoSync()).DeleteItemIfVersion(id, version, cancellationToken).NoSync();

    public async ValueTask<LibrarianItem<TDocument>> MutateItem(string id, Func<TDocument, TDocument> mutation, int maxAttempts = 5,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return await (await GetContainer(cancellationToken).NoSync()).MutateItem<TDocument>(id, current =>
        {
            string? originalId = current.Id;
            TDocument changed = mutation(current);
            if (changed is null || changed.Id != originalId) throw new ArgumentException("A mutation must preserve the document ID.", nameof(mutation));
            return changed;
        }, maxAttempts, cancellationToken).NoSync();
    }

    public ValueTask<LibrarianPage<T>> GetItemsPaged<T>(IQueryable<T> query, int pageSize = 100, string? continuationToken = null,
        CancellationToken cancellationToken = default) => query.ToPageAsync(pageSize, continuationToken, cancellationToken);

    public async ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();
        await container.EnsureIndex(fieldPath, cancellationToken).NoSync();
    }

    public async ValueTask<LibrarianQueryResult<TDocument>> FindByIndex(string fieldPath, object? value, int skip = 0, int take = 100,
        CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();
        return await container.FindByIndex<TDocument>(fieldPath, value, skip, take, cancellationToken).NoSync();
    }

    public async ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();
        return await container.CountByIndex(fieldPath, value, cancellationToken).NoSync();
    }

    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();
        return await container.ExistsByIndex(fieldPath, value, cancellationToken).NoSync();
    }

    public async ValueTask<LibrarianQueryResult<TDocument>> FindRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();
        return await container.FindRangeByIndex<TDocument>(fieldPath, minimum, maximum, descending, skip, take, cancellationToken).NoSync();
    }

    public async ValueTask<IQueryable<T>> BuildQueryable<T>(CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();

        return container.BuildQueryable<T>();
    }

    public List<T> GetItems<T>(IQueryable<T> queryable)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("-- LIBRARIAN: {method} ({type})", nameof(GetItems), typeof(T).Name);

        // We're just materializing it here because the IQueryable has already been built via the container
        return [.. queryable];
    }

    public async ValueTask<TDocument?> GetItem(string id, CancellationToken cancellationToken = default)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("-- LIBRARIAN: {method} ({type}): {id}", nameof(GetItem), typeof(TDocument).Name, id);

        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();

        string? item = await container.GetItem(id, cancellationToken).NoSync();

        if (item == null)
            return null;

        return LibrarianJson.Deserialize<TDocument>(item);
    }

    public async ValueTask<List<TDocument>?> GetAll(CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();

        List<string> items = await container.GetAllItems(cancellationToken).NoSync();

        if (items.Count == 0)
            return null;

        var list = new List<TDocument>(items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string item = items[i];
            var document = LibrarianJson.Deserialize<TDocument>(item);

            if (document != null)
                list.Add(document);
        }

        return list;
    }

    public ValueTask<TDocument?> GetItemByIdNamePair(IdNamePair idNamePair, CancellationToken cancellationToken = default)
    {
        return GetItem(idNamePair.Id, cancellationToken);
    }

    public virtual async ValueTask<string> AddItem(TDocument document, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();

        ArgumentException.ThrowIfNullOrEmpty(document.Id);
        string docSerialized = LibrarianJson.Serialize(document);
        if (_log && Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("-- LIBRARIAN: {method} ({type}): {document}", nameof(AddItem), typeof(TDocument).Name, docSerialized);

        _ = await container.AddItem(document.Id, docSerialized, cancellationToken).NoSync();

        return document.Id;
    }

    public virtual async ValueTask<List<TDocument>> AddItems(List<TDocument> documents, CancellationToken cancellationToken = default)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug("-- LIBRARIAN: {method} ({type})", nameof(AddItems), typeof(TDocument).Name);
        }

        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();

        foreach (TDocument document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrEmpty(document.Id);
            string docSerialized = LibrarianJson.Serialize(document);

            _ = await container.AddItem(document.Id, docSerialized, cancellationToken).NoSync();
        }

        return documents;
    }

    public virtual async ValueTask<string> UpdateItem(TDocument document, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();

        ArgumentException.ThrowIfNullOrEmpty(document.Id);
        string docSerialized = LibrarianJson.Serialize(document);
        if (_log && Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("-- LIBRARIAN: {method} ({type}): {document}", nameof(UpdateItem), typeof(TDocument).Name, docSerialized);

        _ = await container.UpdateItemStrict(document.Id, docSerialized, cancellationToken).NoSync();

        return document.Id;
    }

    public virtual async ValueTask<List<TDocument>> UpdateItems(List<TDocument> documents, CancellationToken cancellationToken = default)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug("-- LIBRARIAN: {method} ({type})", nameof(UpdateItems), typeof(TDocument).Name);
        }

        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();

        for (var i = 0; i < documents.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TDocument document = documents[i];
            ArgumentException.ThrowIfNullOrEmpty(document.Id);
            string docSerialized = LibrarianJson.Serialize(document);

            _ = await container.UpdateItemStrict(document.Id, docSerialized, cancellationToken).NoSync();
        }

        return documents;
    }

    public virtual async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();

        await container.DeleteItem(id, cancellationToken).NoSync();
    }

    public virtual async ValueTask DeleteAll(CancellationToken cancellationToken = default)
    {
        Logger.LogWarning("-- LIBRARIAN: {method} ({type}) ", nameof(DeleteAll), typeof(TDocument).Name);

        ILibrarianContainer container = await GetContainer(cancellationToken).NoSync();

        await container.DeleteAllItems(cancellationToken).NoSync();
    }
}
