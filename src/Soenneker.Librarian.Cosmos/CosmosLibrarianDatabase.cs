using Soenneker.Atomics.ValueBools;
using Soenneker.Asyncs.Semaphores;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Soenneker.Cosmos.Container.Abstract;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Cosmos;

public sealed partial class CosmosLibrarianDatabase : ILibrarianDatabase
{
    private readonly Func<string, CancellationToken, ValueTask<Container>> _getContainer;
    private readonly AsyncSemaphore _gate = new(1);
    private readonly Dictionary<(string Name, string? Partition), CosmosLibrarianContainer> _containers = new();
    private readonly Dictionary<string, Container> _stores = new(StringComparer.Ordinal);
    private ValueAtomicBool _disposed = new(false);
    internal string Key { get; }

    public CosmosLibrarianDatabase(IConfiguration configuration, ICosmosContainerUtil containerUtil)
        : this(containerUtil, configuration["Librarian:Cosmos:Key"] ?? "librarian", configuration["Librarian:Cosmos:ContainerName"] ?? "librarian") { }

    /// <summary>Lazily provisions one physical container per logical name using the shared Cosmos utilities. Names are prefix.key.name, with escaped segments.</summary>
    public CosmosLibrarianDatabase(ICosmosContainerUtil containerUtil, string key, string containerName = "librarian")
    {
        ArgumentNullException.ThrowIfNull(containerUtil);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        Key = key;
        _getContainer = (name, token) => containerUtil.Get(Escape(containerName) + "." + Escape(key) + "." + Escape(name), token);
    }
    /// <summary>Uses a caller-owned container partitioned by /partitionKey. GetContainer must use that container's ID.</summary>
    public CosmosLibrarianDatabase(Container container, string key = "librarian")
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        _getContainer = (name, _) => name == container.Id ? ValueTask.FromResult(container) :
            throw new ArgumentException("A caller-owned Cosmos container must be accessed using its physical ID.", nameof(name));
    }
    private static string Escape(string value) => Uri.EscapeDataString(value).Replace(".", "%2E", StringComparison.Ordinal);
    internal void Check(CancellationToken token = default) { ObjectDisposedException.ThrowIf(_disposed.Value, this); token.ThrowIfCancellationRequested(); }
    private async ValueTask<Container> Store(string name, CancellationToken token)
    {
        Check(token);
        if (!_stores.TryGetValue(name, out Container? store)) _stores.Add(name, store = await _getContainer(name, token).NoSync());
        return store;
    }
    public ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default) => GetPartitionContainer(containerName, null, cancellationToken);
    public ValueTask<ILibrarianContainer> GetContainer(string containerName, string partitionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        return GetPartitionContainer(containerName, partitionKey, cancellationToken);
    }
    private async ValueTask<ILibrarianContainer> GetPartitionContainer(string name, string? partition, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        using (await _gate.Acquire(token).NoSync())
        {
            Container store = await Store(name, token).NoSync();
            if (!_containers.TryGetValue((name, partition), out CosmosLibrarianContainer? container))
                _containers.Add((name, partition), container = new CosmosLibrarianContainer(this, store, name, partition));
            return container;
        }
    }
    public async ValueTask<IQueryable<T>> BuildQueryableAcrossPartitions<T>(string containerName, CancellationToken cancellationToken = default) =>
        (await GetContainer(containerName, cancellationToken).NoSync()).BuildQueryable<T>();
    public ValueTask Save(CancellationToken cancellationToken = default) { Check(cancellationToken); return ValueTask.CompletedTask; }
    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default) { Check(cancellationToken); return ValueTask.CompletedTask; }
    public async ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        using (await _gate.Acquire(cancellationToken).NoSync())
        {
            Check(cancellationToken);
            (string Name, string? Partition)[] keys = _containers.Keys.Where(key => key.Name == containerName).ToArray();
            foreach ((string Name, string? Partition) key in keys) { _containers[key].Dispose(); _containers.Remove(key); }
            return keys.Length != 0;
        }
    }
    public async ValueTask DisposeAsync()
    {
        using (await _gate.Acquire().NoSync())
        {
            if (!_disposed.TrySetTrue()) return;
            foreach (CosmosLibrarianContainer container in _containers.Values) container.Dispose();
            _containers.Clear();
            _stores.Clear();
        }
    }
}
