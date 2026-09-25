using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Asyncs.Locks;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;

namespace Soenneker.Librarian.Core;

/// <summary>Shared single-owner database for providers that atomically replace a complete JSON snapshot.</summary>
/// <remarks>Ordinary mutations remain in memory until Save, UnloadContainer, or disposal. Batches persist before
/// publication. Only one database instance may own a storage address. Stop container operations before disposal.</remarks>
public abstract class SnapshotLibrarianDatabase(ILogger logger) : ILibrarianDatabase
{
    private readonly AsyncLock _gate = new();
    private readonly LibrarianBatchExecutor _batches = new();
    private readonly Dictionary<string, LibrarianContainer> _containers = new(StringComparer.Ordinal);
    private Dictionary<string, List<IdValuePair>>? _snapshot;
    private string? _persistedJson;
    private bool _disposed;

    /// <summary>Reads the persisted snapshot; null means the storage address does not exist yet.</summary>
    protected abstract ValueTask<string?> ReadSnapshot(CancellationToken cancellationToken);

    /// <summary>Atomically replaces the persisted snapshot. Failures must throw.</summary>
    protected abstract ValueTask WriteSnapshot(string json, CancellationToken cancellationToken);

    private async ValueTask Load(CancellationToken token)
    {
        if (_snapshot is not null) return;
        string? json = await ReadSnapshot(token).ConfigureAwait(false);
        var snapshot = json is null ? new(StringComparer.Ordinal) :
            JsonSerializer.Deserialize(json, SnapshotJsonContext.Default.Database) ??
            throw new InvalidDataException("The Librarian snapshot must be a JSON object.");
        foreach ((string name, List<IdValuePair> items) in snapshot)
        {
            if (string.IsNullOrWhiteSpace(name) || items is null)
                throw new InvalidDataException("Invalid container in Librarian snapshot.");
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IdValuePair item in items)
                if (item is null || item.Id is null || item.Value is null || !ids.Add(item.Id))
                    throw new InvalidDataException("Invalid or duplicate document in Librarian snapshot.");
        }
        _persistedJson = JsonSerializer.Serialize(snapshot, SnapshotJsonContext.Default.Database);
        _snapshot = snapshot;
    }

    private LibrarianContainer GetOrCreate(string name)
    {
        if (!_containers.TryGetValue(name, out LibrarianContainer? container))
        {
            container = new LibrarianContainer(name, this, logger, _snapshot!.GetValueOrDefault(name), _batches.Gate);
            _containers.Add(name, container);
        }
        return container;
    }

    public async ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        using (await _gate.Lock(cancellationToken).ConfigureAwait(false))
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Load(cancellationToken).ConfigureAwait(false);
            return GetOrCreate(containerName);
        }
    }

    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default)
    {
        // Save captures every loaded container under the mutation gate, including mutations whose notification is delayed.
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.CompletedTask;
    }

    public async ValueTask Save(CancellationToken cancellationToken = default)
    {
        using (await _gate.Lock(cancellationToken).ConfigureAwait(false))
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await SavePending(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask SavePending(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_snapshot is null) return;
        using (await _batches.Gate.Lock(token).ConfigureAwait(false))
        {
            var snapshots = new Dictionary<string, List<IdValuePair>>(StringComparer.Ordinal);
            foreach ((string name, LibrarianContainer container) in _containers)
                snapshots.Add(name, container.SnapshotForBatch());
            await Persist(snapshots, token).ConfigureAwait(false);
        }
    }

    private async ValueTask Persist(IReadOnlyDictionary<string, List<IdValuePair>> snapshots, CancellationToken token)
    {
        var next = new Dictionary<string, List<IdValuePair>>(_snapshot!, StringComparer.Ordinal);
        foreach ((string name, List<IdValuePair> items) in snapshots) next[name] = items;
        string json = JsonSerializer.Serialize(next, SnapshotJsonContext.Default.Database);
        if (string.Equals(json, _persistedJson, StringComparison.Ordinal)) return;
        await WriteSnapshot(json, token).ConfigureAwait(false);
        _snapshot = next;
        _persistedJson = json;
    }

    public async ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using (await _gate.Lock(cancellationToken).ConfigureAwait(false))
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Load(cancellationToken).ConfigureAwait(false);
            foreach (LibrarianWrite write in batch.Writes) GetOrCreate(write.Container);
            foreach (LibrarianCondition condition in batch.Conditions) GetOrCreate(condition.Container);
            return await _batches.Execute(batch, _containers, Persist, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        using (await _gate.Lock(cancellationToken).ConfigureAwait(false))
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_containers.ContainsKey(containerName)) return false;
            await SavePending(cancellationToken).ConfigureAwait(false);
            _containers.Remove(containerName, out LibrarianContainer? container);
            container!.Dispose();
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (await _gate.Lock(CancellationToken.None).ConfigureAwait(false))
        {
            if (_disposed) return;
            // Keep state available for retry if flushing fails.
            await SavePending(CancellationToken.None).ConfigureAwait(false);
            _disposed = true;
            foreach (LibrarianContainer container in _containers.Values) container.Dispose();
            _containers.Clear();
            _snapshot = null;
            _persistedJson = null;
        }
    }
}
