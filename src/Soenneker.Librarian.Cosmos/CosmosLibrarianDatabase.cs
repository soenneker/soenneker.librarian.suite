using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Scripts;
using Microsoft.Extensions.Configuration;
using Soenneker.Cosmos.Container.Abstract;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;

namespace Soenneker.Librarian.Cosmos;

public sealed class CosmosLibrarianDatabase : ILibrarianDatabase
{
    private readonly Func<CancellationToken, ValueTask<Container>> _getContainer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, CosmosLibrarianContainer> _containers = new(StringComparer.Ordinal);
    private Container? _store;
    private bool _scriptReady;
    private volatile bool _disposed;
    internal string Key { get; }
    internal PartitionKey Partition => new(Key);

    public CosmosLibrarianDatabase(IConfiguration configuration, ICosmosContainerUtil containerUtil)
        : this(containerUtil, configuration["Librarian:Cosmos:Key"] ?? "librarian",
            configuration["Librarian:Cosmos:ContainerName"] ?? "librarian") { }

    /// <summary>Uses the shared Cosmos utilities to lazily ensure the configured database and physical container.</summary>
    public CosmosLibrarianDatabase(ICosmosContainerUtil containerUtil, string key, string containerName = "librarian")
    {
        ArgumentNullException.ThrowIfNull(containerUtil);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        Key = key;
        _getContainer = token => containerUtil.Get(containerName, token);
    }

    /// <summary>Uses an already provisioned, caller-owned container partitioned by /partitionKey.</summary>
    public CosmosLibrarianDatabase(Container container, string key = "librarian")
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        _getContainer = _ => ValueTask.FromResult(container);
    }

    internal void Check(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
    }
    private async ValueTask<Container> EnsureStore(CancellationToken token)
    {
        Check(token);
        return _store ??= await _getContainer(token).ConfigureAwait(false);
    }
    public async ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Container store = await EnsureStore(cancellationToken).ConfigureAwait(false);
            if (!_containers.TryGetValue(containerName, out CosmosLibrarianContainer? container))
                _containers.Add(containerName, container = new CosmosLibrarianContainer(this, store, containerName));
            return container;
        }
        finally { _gate.Release(); }
    }

    public ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Check(cancellationToken);
        var writes = new List<object>(batch.Writes.Count);
        foreach (LibrarianWrite write in batch.Writes)
            writes.Add(new { id = CosmosDocument.IdFor(write.Container, write.Id), document = write.Value is null ? null :
                CosmosDocument.Create(Key, write.Container, write.Id, write.Value) });
        var conditions = new List<object>(batch.Conditions.Count);
        foreach (LibrarianCondition condition in batch.Conditions)
            conditions.Add(new { id = CosmosDocument.IdFor(condition.Container, condition.Id), expected = condition.ExpectedValue });
        return ExecuteScript(JsonSerializer.Serialize(new { partitionKey = Key, writes, conditions }), cancellationToken);
    }
    internal ValueTask<bool> Clear(string containerName, CancellationToken token) =>
        ExecuteScript(JsonSerializer.Serialize(new { clear = containerName }), token);

    private async ValueTask<bool> ExecuteScript(string request, CancellationToken token)
    {
        Container store;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            store = await EnsureStore(token).ConfigureAwait(false);
            if (!_scriptReady)
            {
                try
                {
                    await store.Scripts.CreateStoredProcedureAsync(new StoredProcedureProperties
                    { Id = CosmosBatchScript.Id, Body = CosmosBatchScript.Body }, cancellationToken: token).ConfigureAwait(false);
                }
                catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict) { }
                _scriptReady = true;
            }
        }
        finally { _gate.Release(); }
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                StoredProcedureExecuteResponse<bool> result = await store.Scripts.ExecuteStoredProcedureAsync<bool>(
                    CosmosBatchScript.Id, Partition, [request], cancellationToken: token).ConfigureAwait(false);
                return result.Resource;
            }
            // These server responses confirm a rolled-back transaction. Never retry an uncertain transport outcome.
            catch (CosmosException exception) when (attempt < 4 &&
                (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed || (int)exception.StatusCode == 449)) { }
        }
    }

    public ValueTask Save(CancellationToken cancellationToken = default) { Check(cancellationToken); return ValueTask.CompletedTask; }
    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default) { Check(); return ValueTask.CompletedTask; }
    public async ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Check(cancellationToken);
            if (!_containers.Remove(containerName, out CosmosLibrarianContainer? container)) return false;
            container.Dispose();
            return true;
        }
        finally { _gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (CosmosLibrarianContainer container in _containers.Values) container.Dispose();
            _containers.Clear();
            _store = null;
        }
        finally { _gate.Release(); }
    }
}
