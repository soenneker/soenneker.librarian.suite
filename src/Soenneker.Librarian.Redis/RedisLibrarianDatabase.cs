using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Asyncs.Locks;
using Soenneker.Atomics.ValueBools;
using Soenneker.Extensions.Configuration;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions;
using Soenneker.Redis.Client.Abstract;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianDatabase : ILibrarianDatabase
{
    internal string StoragePrefix { get; }
    private readonly IRedisClient? _client;
    private readonly Func<CancellationToken, ValueTask<IDatabase>>? _storeFactory;
    private readonly int _database;
    private readonly AsyncLock _gate = new();
    private readonly Dictionary<string, ILibrarianContainer> _containers = new(StringComparer.Ordinal);
    private ValueAtomicBool _disposed = new(false);

    public RedisLibrarianDatabase(IConfiguration configuration, IRedisClient redisClient, ILogger<RedisLibrarianDatabase> logger)
        : this(configuration.GetValueStrict<string>("Librarian:Redis:Key"), redisClient, logger,
            configuration.GetValue<int?>("Librarian:Redis:Database") ?? -1,
            configuration.GetValue<string>("Librarian:Redis:KeyPrefix") ?? "librarian") { }

    public RedisLibrarianDatabase(string key, IRedisClient redisClient, ILogger logger, int database = -1,
        string keyPrefix = "librarian")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(redisClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(database, -1);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        StoragePrefix = RedisIndexValue.KeySegment(keyPrefix) + ":{" + RedisIndexValue.KeySegment(key) + "}:containers:";
        _client = redisClient;
        _database = database;
    }

    /// <summary>Uses a caller-owned database connection factory. The namespace must be exclusive to this database.</summary>
    public RedisLibrarianDatabase(string key, Func<CancellationToken, ValueTask<IDatabase>> storeFactory,
        string keyPrefix = "librarian")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(storeFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        StoragePrefix = RedisIndexValue.KeySegment(keyPrefix) + ":{" + RedisIndexValue.KeySegment(key) + "}:containers:";
        _storeFactory = storeFactory;
    }

    internal async ValueTask<IDatabase> GetStore(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        token.ThrowIfCancellationRequested();
        if (_storeFactory is not null) return await _storeFactory(token).NoSync();
        ConnectionMultiplexer connection = await _client!.Get(token).NoSync();
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        return connection.GetDatabase(_database);
    }

    public async ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        using (await _gate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            if (!_containers.TryGetValue(containerName, out ILibrarianContainer? container))
                _containers.Add(containerName, container = new RedisLibrarianContainer(containerName, this));
            return container;
        }
    }

    public ValueTask Save(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        using (await _gate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed.Value, this);
            if (!_containers.Remove(containerName, out ILibrarianContainer? container)) return false;
            container.Dispose();
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (await _gate.Lock(CancellationToken.None).NoSync())
        {
            if (!_disposed.TrySetTrue()) return;
            foreach (ILibrarianContainer container in _containers.Values) container.Dispose();
            _containers.Clear();
        }
    }
}
