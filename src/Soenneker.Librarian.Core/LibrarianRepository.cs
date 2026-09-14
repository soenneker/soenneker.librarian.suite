using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Documents.Document;
using Soenneker.Dtos.IdNamePair;
using Soenneker.Enums.JsonOptions;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.Json;
using Soenneker.Utils.Method;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;

namespace Soenneker.Librarian.Core;

public class LibrarianRepository<TDocument> : ILibrarianRepository<TDocument> where TDocument : Document
{
    private readonly ILibrarianDatabase _database;

    protected ILogger<LibrarianRepository<TDocument>> Logger { get; }

    private readonly bool _log;

    protected string ContainerName { get; set; }

    public LibrarianRepository(IConfiguration config, ILogger<LibrarianRepository<TDocument>> logger, ILibrarianDatabase database, string containerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        _database = database;
        ContainerName = containerName;
        Logger = logger;

        _log = config.GetValue<bool>("Librarian:Log");
    }

    public async ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();
        await container.EnsureIndex(fieldPath, cancellationToken).NoSync();
    }

    public async ValueTask<LibrarianQueryResult<TDocument>> FindByIndex(string fieldPath, object? value, int skip = 0, int take = 100,
        CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();
        return await container.FindByIndex<TDocument>(fieldPath, value, skip, take, cancellationToken).NoSync();
    }

    public async ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();
        return await container.CountByIndex(fieldPath, value, cancellationToken).NoSync();
    }

    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();
        return await container.ExistsByIndex(fieldPath, value, cancellationToken).NoSync();
    }

    public async ValueTask<LibrarianQueryResult<TDocument>> FindRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();
        return await container.FindRangeByIndex<TDocument>(fieldPath, minimum, maximum, descending, skip, take, cancellationToken).NoSync();
    }

    public async ValueTask<IQueryable<T>> BuildQueryable<T>(CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();

        return container.BuildQueryable<T>();
    }

    public List<T> GetItems<T>(IQueryable<T> queryable)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("-- LIBRARIAN: {method} ({type})", MethodUtil.Get(), typeof(T).Name);

        // We're just materializing it here because the IQueryable has already been built via the container
        return [.. queryable];
    }

    public async ValueTask<TDocument?> GetItem(string id, CancellationToken cancellationToken = default)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
            Logger.LogDebug("-- LIBRARIAN: {method} ({type}): {id}", MethodUtil.Get(), typeof(TDocument).Name, id);

        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();

        string? item = await container.GetItem(id, cancellationToken).NoSync();

        if (item == null)
            return null;

        return JsonUtil.Deserialize<TDocument>(item);
    }

    public async ValueTask<List<TDocument>?> GetAll(CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();

        List<string> items = await container.GetAllItems(cancellationToken).NoSync();

        if (items.Count == 0)
            return null;

        var list = new List<TDocument>(items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string item = items[i];
            var document = JsonUtil.Deserialize<TDocument>(item);

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
        if (_log && Logger.IsEnabled(LogLevel.Debug))
        {
            string? serialized = JsonUtil.Serialize(document, JsonOptionType.Pretty);
            Logger.LogDebug("-- LIBRARIAN: {method} ({type}): {document}", MethodUtil.Get(), typeof(TDocument).Name, serialized);
        }

        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();

        ArgumentException.ThrowIfNullOrEmpty(document.Id);
        string? docSerialized = JsonUtil.Serialize(document);

        if (docSerialized == null)
            throw new Exception("Failed to serialize document");

        _ = await container.AddItem(document.Id, docSerialized, cancellationToken).NoSync();

        return document.Id;
    }

    public virtual async ValueTask<List<TDocument>> AddItems(List<TDocument> documents, CancellationToken cancellationToken = default)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug("-- LIBRARIAN: {method} ({type})", MethodUtil.Get(), typeof(TDocument).Name);
        }

        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();

        foreach (TDocument document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentException.ThrowIfNullOrEmpty(document.Id);
            string? docSerialized = JsonUtil.Serialize(document);

            if (docSerialized == null)
                throw new Exception("Failed to serialize document");

            _ = await container.AddItem(document.Id, docSerialized, cancellationToken).NoSync();
        }

        return documents;
    }

    public virtual async ValueTask<string> UpdateItem(TDocument document, CancellationToken cancellationToken = default)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
        {
            string? serialized = JsonUtil.Serialize(document, JsonOptionType.Pretty);
            Logger.LogDebug("-- LIBRARIAN: {method} ({type}): {document}", MethodUtil.Get(), typeof(TDocument).Name, serialized);
        }

        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();

        ArgumentException.ThrowIfNullOrEmpty(document.Id);
        string? docSerialized = JsonUtil.Serialize(document);

        if (docSerialized == null)
            throw new Exception("Failed to serialize document");

        _ = await container.UpdateItemStrict(document.Id, docSerialized, cancellationToken).NoSync();

        return document.Id;
    }

    public virtual async ValueTask<List<TDocument>> UpdateItems(List<TDocument> documents, CancellationToken cancellationToken = default)
    {
        if (_log && Logger.IsEnabled(LogLevel.Debug))
        {
            Logger.LogDebug("-- LIBRARIAN: {method} ({type})", MethodUtil.Get(), typeof(TDocument).Name);
        }

        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();

        for (var i = 0; i < documents.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TDocument document = documents[i];
            ArgumentException.ThrowIfNullOrEmpty(document.Id);
            string? docSerialized = JsonUtil.Serialize(document);

            if (docSerialized == null)
                throw new Exception("Failed to serialize document");

            _ = await container.UpdateItemStrict(document.Id, docSerialized, cancellationToken).NoSync();
        }

        return documents;
    }

    public virtual async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default)
    {
        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();

        await container.DeleteItem(id, cancellationToken).NoSync();
    }

    public virtual async ValueTask DeleteAll(CancellationToken cancellationToken = default)
    {
        Logger.LogWarning("-- LIBRARIAN: {method} ({type}) ", MethodUtil.Get(), typeof(TDocument).Name);

        ILibrarianContainer container = await _database.GetContainer(ContainerName, cancellationToken).NoSync();

        await container.DeleteAllItems(cancellationToken).NoSync();
    }
}
