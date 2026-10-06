using System.Text.Json;
using MongoDB.Bson.Serialization;
using Soenneker.Atomics.ValueBools;
using Soenneker.Asyncs.Semaphores;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Mongo;

public sealed partial class MongoLibrarianDatabase : ILibrarianDatabase
{
    private readonly AsyncSemaphore _gate = new(1);
    private readonly Dictionary<(string Name, string? Partition), MongoLibrarianContainer> _containers = new();
    private readonly ConcurrentDictionary<string, IMongoCollection<BsonDocument>> _collections = new(StringComparer.Ordinal);
    private readonly Func<Type, JsonSerializerOptions, IBsonSerializer> _serializer;
    private readonly IMongoDatabase _database;
    private readonly IMongoClient _client;
    private readonly bool _ownsClient;
    private readonly string _prefix;
    private ValueAtomicBool _disposed = new(false);

    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime BSON serializer discovery requires preserved document members. Supply explicit serializer factories instead.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Runtime BSON serializer discovery constructs generic types. Supply explicit serializer factories instead.")]
    public MongoLibrarianDatabase(IConfiguration configuration) : this(Required(configuration, "ConnectionString"), Required(configuration, "DatabaseName"),
        Required(configuration, "Key"), configuration["Librarian:Mongo:CollectionName"] ?? "librarian") { }
    /// <summary>Creates a client and uses one native collection per logical container. Transactions require a replica set.</summary>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime BSON serializer discovery requires preserved document members. Supply explicit serializer factories instead.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Runtime BSON serializer discovery constructs generic types. Supply explicit serializer factories instead.")]
    public MongoLibrarianDatabase(string connectionString, string databaseName, string key, string collectionName = "librarian")
        : this(new MongoClient(connectionString).GetDatabase(databaseName), key, collectionName) { _ownsClient = true; }
    /// <summary>Uses a caller-owned database. Collection names are prefix.key.name, with escaped segments.</summary>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Runtime BSON serializer discovery requires preserved document members. Supply explicit serializer factories instead.")]
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Runtime BSON serializer discovery constructs generic types. Supply explicit serializer factories instead.")]
    public MongoLibrarianDatabase(IMongoDatabase database, string key, string collectionName = "librarian")
        : this(database, key, collectionName, MongoJsonSerializers.Create) { }

    /// <summary>Uses explicit BSON serializer factories for typed queries.</summary>
    public MongoLibrarianDatabase(IMongoDatabase database, string key, MongoJsonSerializerRegistry serializers, string collectionName = "librarian")
        : this(database, key, collectionName, (serializers ?? throw new ArgumentNullException(nameof(serializers))).Create) { }

    private MongoLibrarianDatabase(IMongoDatabase database, string key, string collectionName,
        Func<Type, JsonSerializerOptions, IBsonSerializer> serializer)
    {
        _serializer = serializer;
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        _database = database;
        _client = database.Client;
        _prefix = Escape(collectionName) + "." + Escape(key) + ".";
    }
    private static string Escape(string value) => Uri.EscapeDataString(value).Replace(".", "%2E", StringComparison.Ordinal);
    private static string Required(IConfiguration configuration, string name) => configuration[$"Librarian:Mongo:{name}"] ?? throw new InvalidOperationException($"Missing configuration: Librarian:Mongo:{name}");
    internal void Check(CancellationToken token = default) { ObjectDisposedException.ThrowIf(_disposed.Value, this); token.ThrowIfCancellationRequested(); }
    internal IMongoCollection<BsonDocument> Collection(string name) => _collections.GetOrAdd(name, static (key, owner) =>
        owner._database.GetCollection<BsonDocument>(owner._prefix + Escape(key))
            .WithReadConcern(ReadConcern.Majority).WithReadPreference(ReadPreference.Primary).WithWriteConcern(WriteConcern.WMajority), this);
    internal IQueryable<T> BuildQueryable<T>(string name, string? partition)
    {
        IQueryable<BsonDocument> query = Collection(name).AsQueryable(new AggregateOptions { Collation = Collation.Simple,
            TranslationOptions = new ExpressionTranslationOptions { EnableClientSideProjections = false } });
        if (partition is not null) query = query.Where(document => document["partitionKey"] == partition);
        return query.As<BsonDocument, T>((MongoDB.Bson.Serialization.IBsonSerializer<T>)_serializer(typeof(T), LibrarianJson.Contract(typeof(T)).Options));
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
            Check(token);
            if (!_containers.TryGetValue((name, partition), out MongoLibrarianContainer? container))
                _containers.Add((name, partition), container = new MongoLibrarianContainer(name, this, partition));
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
            _collections.TryRemove(containerName, out _);
            return keys.Length != 0;
        }
    }
    public async ValueTask DisposeAsync()
    {
        using (await _gate.Acquire().NoSync())
        {
            if (!_disposed.TrySetTrue()) return;
            foreach (MongoLibrarianContainer container in _containers.Values) container.Dispose();
            _containers.Clear();
            _collections.Clear();
            if (_ownsClient) _client.Dispose();
        }
    }
}
