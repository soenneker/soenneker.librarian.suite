using System;
using System.Linq;
using Soenneker.Librarian.Abstractions.Transactions;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Core;
using Soenneker.Asyncs.Locks;
using Soenneker.Atomics.ValueBools;
using Soenneker.Extensions.ValueTask;

namespace Soenneker.Librarian.Memory;

public sealed class MemoryLibrarianDatabase(ILogger<MemoryLibrarianDatabase> logger) : ILibrarianDatabase
{
    private readonly AsyncLock _gate = new();
    private readonly LibrarianBatchExecutor _batches = new();

    private readonly Dictionary<string, ILibrarianContainer> _containers = new(StringComparer.Ordinal);

    private ValueAtomicBool _disposed = new(false);

    public async ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        cancellationToken.ThrowIfCancellationRequested();
        using (await _gate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            if (!_containers.TryGetValue(containerName, out ILibrarianContainer? container))
            {
                container = new LibrarianContainer(containerName, this, logger, mutationGate: _batches.Gate);
                _containers.Add(containerName, container);
            }
            return container;
        }
    }

    public async ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using (await _gate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            foreach (string name in batch.Writes.Select(write => write.Container).Concat(batch.Conditions.Select(condition => condition.Container)).Distinct(StringComparer.Ordinal))
                if (!_containers.ContainsKey(name)) _containers.Add(name, new LibrarianContainer(name, this, logger, mutationGate: _batches.Gate));
            Dictionary<string, LibrarianContainer> containers = _containers.ToDictionary(pair => pair.Key, pair => (LibrarianContainer)pair.Value, StringComparer.Ordinal);
            return await _batches.Execute(batch, containers, cancellationToken: cancellationToken).NoSync();
        }
    }

    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        return ValueTask.CompletedTask;
    }

    public ValueTask Save(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        cancellationToken.ThrowIfCancellationRequested();
        using (await _gate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            if (!_containers.Remove(containerName, out ILibrarianContainer? container))
                return false;
            container.Dispose();
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (await _gate.Lock(CancellationToken.None).NoSync())
        {
            if (!_disposed.TrySetTrue())
                return;
            foreach (ILibrarianContainer container in _containers.Values)
                container.Dispose();
            _containers.Clear();
        }
    }
}
