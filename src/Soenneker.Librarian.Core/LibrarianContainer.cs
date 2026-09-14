using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Atomics.ValueBools;
using Soenneker.Asyncs.Locks;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Core.Indexes;

namespace Soenneker.Librarian.Core;

public sealed partial class LibrarianContainer : ILibrarianContainer
{
    private readonly string _containerName;
    private readonly ILibrarianDatabase _database;
    private readonly ILogger _logger;

    private ConcurrentDictionary<string, string> _items;
    private readonly ConcurrentDictionary<Type, object> _queryRoots = new();

    private ValueAtomicBool _disposed = new(false);

    public LibrarianContainer(string containerName, ILibrarianDatabase database, ILogger logger, List<IdValuePair>? existingData = null, AsyncLock? mutationGate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(logger);
        _containerName = containerName;
        _database = database;
        _logger = logger;
        _mutationGate = mutationGate ?? new AsyncLock();

        if (existingData is null || existingData.Count == 0)
        {
            _items = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _logger.LogWarning("No existing data found for container ({containerName})", containerName);
            return;
        }

        // concurrencyLevel: a reasonable default; capacity: existing count
        _items = new ConcurrentDictionary<string, string>(
            concurrencyLevel: Environment.ProcessorCount,
            capacity: existingData.Count,
            comparer: StringComparer.OrdinalIgnoreCase);

        foreach (IdValuePair data in existingData)
        {
            if (!_items.TryAdd(data.Id, data.Value))
                _logger.LogWarning("Duplicate key detected: {key} in container ({containerName})", data.Id, containerName);
        }

        _logger.LogDebug("Loaded {count} items for container ({containerName})", _items.Count, containerName);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed.Value)
            throw new ObjectDisposedException(nameof(LibrarianContainer), $"Container '{_containerName}' is disposed.");
    }

    public async ValueTask<string> AddItem(string id, string item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(id);
            ArgumentNullException.ThrowIfNull(item);
            if (_items.ContainsKey(id))
                throw new InvalidOperationException($"Failed to add item ({id})");
            IndexKey?[] keys = PrepareIndexKeys(item);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _items[id] = item;
                _queryScanSnapshot = null;
                ApplyIndexKeys(id, keys);
                UpdateAutomaticIndexes(id, item);
            }
            finally { Array.Clear(keys); }
        }
        await _database.MarkDirty(_containerName, CancellationToken.None).NoSync();
        return item;
    }

    public IQueryable<T> BuildQueryable<T>()
    {
        ThrowIfDisposed();
        return (IQueryable<T>)_queryRoots.GetOrAdd(typeof(T), static (_, container) =>
            new Indexes.LibrarianQueryable<T>(new Indexes.LibrarianQueryProvider<T>(container)), this);
    }

    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default)
    {
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return _items.GetValueOrDefault(id);
        }
    }

    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).NoSync() ?? throw new KeyNotFoundException($"Could not find item ({id})");

    public ValueTask<string?> UpdateItem(string id, string item, CancellationToken cancellationToken = default)
    {
        return Update(id, item, false, cancellationToken);
    }

    public async ValueTask<string> UpdateItemStrict(string id, string item, CancellationToken cancellationToken = default)
    {
        return (await Update(id, item, true, cancellationToken).NoSync())!;
    }

    private async ValueTask<string?> Update(string id, string item, bool strict, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            ArgumentNullException.ThrowIfNull(item);
            if (!_items.TryGetValue(id, out string? existing))
            {
                if (strict)
                    throw new KeyNotFoundException($"Could not find item ({id})");
                return null;
            }
            if (string.Equals(existing, item, StringComparison.Ordinal))
                return item;
            IndexKey?[] keys = PrepareIndexKeys(item);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _items[id] = item;
                _queryScanSnapshot = null;
                ApplyIndexKeys(id, keys);
                UpdateAutomaticIndexes(id, item);
            }
            finally { Array.Clear(keys); }
        }
        await _database.MarkDirty(_containerName, CancellationToken.None).NoSync();
        return item;
    }

    public async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_items.TryRemove(id, out _))
                throw new KeyNotFoundException($"Failed to delete item ({id})");
            _queryScanSnapshot = null;
            foreach (DocumentIndex index in _indexes.Values)
                index.Remove(id);
            foreach (AutomaticIndex index in _automaticIndexes.Values) index.Index.Remove(id);
        }
        await _database.MarkDirty(_containerName, CancellationToken.None).NoSync();
    }

    public async ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default)
    {
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return [.. _items.Values];
        }
    }

    public async ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default)
    {
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return [.. _items.Keys];
        }
    }

    public async ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default)
    {
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return SnapshotForBatch();
        }
    }

    public async ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using (await _mutationGate.Lock(cancellationToken).NoSync())
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            _items.Clear();
            _queryScanSnapshot = null;
            foreach (DocumentIndex index in _indexes.Values)
                index.Clear();
            foreach (AutomaticIndex index in _automaticIndexes.Values) index.Index.Clear();
        }
        await _database.MarkDirty(_containerName, CancellationToken.None).NoSync();
    }

    public void Dispose()
    {
        // first caller wins
        if (!_disposed.TrySetTrue())
            return;

        _logger.LogDebug("Disposing container ({containerName})", _containerName);

        // The owner must stop operations before disposal, as required by the container contract.
        _indexes.Clear();
        _automaticIndexes.Clear();
        _automaticIndexGroups.Clear();
        _queryRoots.Clear();
        _queryScanSnapshot = null;
        _preparedKeys = Array.Empty<Indexes.IndexKey?>();

        _items.Clear();
    }
}
