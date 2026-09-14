using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Asyncs.Locks;
using Soenneker.Extensions.Configuration;
using Soenneker.Extensions.ValueTask;
using Soenneker.Hashing.Sha256;
using Soenneker.Hashing.Sha256.Abstract;
using Soenneker.Librarian.Abstractions;
using Soenneker.Redis.Client.Abstract;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianDatabase : ILibrarianDatabase
{
    private readonly string _key;
    private readonly IRedisClient? _client;
    private readonly Func<CancellationToken, ValueTask<IDatabase>>? _storeFactory;
    private readonly int _database;
    private readonly ISha256HashingUtil _sha256HashingUtil;
    private readonly AsyncLock _gate = new();
    private readonly Dictionary<string, ILibrarianContainer> _containers = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    public RedisLibrarianDatabase(IConfiguration configuration, IRedisClient redisClient, ILogger<RedisLibrarianDatabase> logger,
        ISha256HashingUtil? sha256HashingUtil = null)
        : this(configuration.GetValueStrict<string>("Librarian:Redis:Key"), redisClient, logger,
            configuration.GetValue<int?>("Librarian:Redis:Database") ?? -1, sha256HashingUtil) { }

    public RedisLibrarianDatabase(string key, IRedisClient redisClient, ILogger logger, int database = -1, ISha256HashingUtil? sha256HashingUtil = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(redisClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThan(database, -1);
        _key = key;
        _client = redisClient;
        _database = database;
        _sha256HashingUtil = sha256HashingUtil ?? new Sha256HashingUtil();
    }

    /// <summary>Uses a caller-owned database connection factory. The namespace must be exclusive to this database.</summary>
    public RedisLibrarianDatabase(string key, Func<CancellationToken, ValueTask<IDatabase>> storeFactory,
        ISha256HashingUtil? sha256HashingUtil = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(storeFactory);
        _key = key;
        _storeFactory = storeFactory;
        _sha256HashingUtil = sha256HashingUtil ?? new Sha256HashingUtil();
    }

    internal async ValueTask<IDatabase> GetStore(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        if (_storeFactory is not null) return await _storeFactory(token).NoSync();
        ConnectionMultiplexer connection = await _client!.Get(token).NoSync();
        token.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        return connection.GetDatabase(_database);
    }

    public async ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        using (await _gate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_containers.TryGetValue(containerName, out ILibrarianContainer? container))
                _containers.Add(containerName, container = new RedisLibrarianContainer(_key, containerName, this, _sha256HashingUtil));
            return container;
        }
    }

    public ValueTask Save(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask MarkDirty(string containerName, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> UnloadContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        using (await _gate.Lock(cancellationToken).NoSync())
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_containers.Remove(containerName, out ILibrarianContainer? container)) return false;
            container.Dispose();
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using (await _gate.Lock(CancellationToken.None).NoSync())
        {
            if (_disposed) return;
            _disposed = true;
            foreach (ILibrarianContainer container in _containers.Values) container.Dispose();
            _containers.Clear();
        }
    }
}
