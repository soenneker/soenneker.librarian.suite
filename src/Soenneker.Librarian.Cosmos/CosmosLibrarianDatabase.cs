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
    internal CosmosLibrarianOptions Options { get; }

    public CosmosLibrarianDatabase(IConfiguration configuration, ICosmosContainerUtil containerUtil, CosmosLibrarianOptions? options = null)
        : this(containerUtil, configuration["Librarian:Cosmos:Key"] ?? "librarian", configuration["Librarian:Cosmos:ContainerName"] ?? "librarian", options) { }

    /// <summary>Lazily provisions one physical container per logical name using the shared Cosmos utilities. Names are prefix.key.name, with escaped segments.</summary>
    public CosmosLibrarianDatabase(ICosmosContainerUtil containerUtil, string key, string containerName = "librarian", CosmosLibrarianOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(containerUtil);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        Key = key;
        Options = options ?? new CosmosLibrarianOptions();
        _getContainer = (name, token) => containerUtil.Get(Options.ContainerNameResolver?.Invoke(name) ?? (Escape(containerName) + "." + Escape(key) + "." + Escape(name)), token);
    }
    /// <summary>Uses a caller-owned container partitioned by /partitionKey. GetContainer must use that container's ID.</summary>
    public CosmosLibrarianDatabase(Container container, string key = "librarian", CosmosLibrarianOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        Options = options ?? new CosmosLibrarianOptions();
        _getContainer = (name, _) => name == container.Id ? ValueTask.FromResult(container) :
            throw new ArgumentException("A caller-owned Cosmos container must be accessed using its physical ID.", nameof(name));
    }
    /// <summary>Uses a caller-owned resolver for multiple existing containers. The caller owns their clients.</summary>
    public CosmosLibrarianDatabase(Func<string, CancellationToken, ValueTask<Container>> containerResolver,
        string key = "librarian", CosmosLibrarianOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(containerResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _getContainer = containerResolver;
        Key = key;
        Options = options ?? new CosmosLibrarianOptions();
    }

    /// <summary>Obtains a native Cosmos handle for patch operations, optionally scoped to a partition.</summary>
    public async ValueTask<ICosmosLibrarianContainer> GetCosmosContainer(string containerName, string? partitionKey = null,
        CancellationToken cancellationToken = default) =>
        (ICosmosLibrarianContainer)await (partitionKey is null ? GetContainer(containerName, cancellationToken)
            : GetContainer(containerName, partitionKey, cancellationToken)).NoSync();

    internal ItemRequestOptions ItemOptions(string name, string operation, string? version = null)
    {
        ItemRequestOptions result = Options.ItemRequestOptionsFactory?.Invoke(name, operation) ?? new ItemRequestOptions();
        if (version is not null) { result.IfMatchEtag = version; result.IfNoneMatchEtag = null; }
        return result;
    }

    internal QueryRequestOptions QueryOptions(string name, string? partition)
    {
        QueryRequestOptions result = Options.QueryRequestOptionsFactory?.Invoke(name) ?? new QueryRequestOptions();
        result.PartitionKey = partition is null ? null : new PartitionKey(partition);
        return result;
    }

    internal void Observe(string name, string operation, System.Net.HttpStatusCode status, Headers headers, CosmosDiagnostics? diagnostics)
    {
        if (Options.Observer is null) return;
        try
        {
            Options.Observer.OnResponse(new CosmosLibrarianResponse(name, operation, status, headers.RequestCharge,
                headers.ActivityId, headers.Session, headers.ETag, diagnostics?.ToString()));
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Cosmos Librarian observer failed: {0}", exception);
        }
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
