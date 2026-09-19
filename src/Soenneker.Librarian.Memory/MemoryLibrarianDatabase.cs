using System;
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

    private readonly Dictionary<string, LibrarianContainer> _containers = new(StringComparer.Ordinal);

    private ValueAtomicBool _disposed = new(false);

    public async ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        cancellationToken.ThrowIfCancellationRequested();
        using (await _gate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            return GetOrCreateContainer(containerName);
        }
    }

    // Called only while holding _gate.
    private LibrarianContainer GetOrCreateContainer(string name)
    {
        if (!_containers.TryGetValue(name, out LibrarianContainer? container))
        {
            container = new LibrarianContainer(name, this, logger, mutationGate: _batches.Gate);
            _containers.Add(name, container);
        }
        return container;
    }

    public async ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using (await _gate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            foreach (LibrarianWrite write in batch.Writes) GetOrCreateContainer(write.Container);
            foreach (LibrarianCondition condition in batch.Conditions) GetOrCreateContainer(condition.Container);
            return await _batches.Execute(batch, _containers, cancellationToken: cancellationToken).NoSync();
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
            if (!_containers.Remove(containerName, out LibrarianContainer? container))
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
            foreach (LibrarianContainer container in _containers.Values)
                container.Dispose();
            _containers.Clear();
        }
    }
}
