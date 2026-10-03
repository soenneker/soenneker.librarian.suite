using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;

namespace Soenneker.Librarian.Mongo;

public sealed class MongoLibrarianDatabase : ILibrarianDatabase
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, MongoLibrarianContainer> _containers = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly IMongoClient _client;
    private readonly bool _ownsClient;
    private readonly string _key;
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private bool _initialized;
    private string MetadataId => _key + "!metadata";

    public MongoLibrarianDatabase(IConfiguration configuration) : this(
        Required(configuration, "ConnectionString"), Required(configuration, "DatabaseName"), Required(configuration, "Key"),
        configuration["Librarian:Mongo:CollectionName"] ?? "librarian") { }

    /// <summary>Creates a MongoDB provider owning its client. MongoDB must support multi-document transactions.</summary>
    public MongoLibrarianDatabase(string connectionString, string databaseName, string key, string collectionName = "librarian")
        : this(CreateClient(connectionString, databaseName, key, collectionName).GetDatabase(databaseName), key, collectionName)
    { _ownsClient = true; }

    /// <summary>Uses a caller-owned MongoDB database. The key isolates a logical Librarian database within the collection.</summary>
    public MongoLibrarianDatabase(IMongoDatabase database, string key, string collectionName = "librarian")
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        _client = database.Client;
        _key = MongoDocument.Field(key);
        _collection = database.GetCollection<BsonDocument>(collectionName).WithReadConcern(ReadConcern.Majority)
            .WithReadPreference(ReadPreference.Primary).WithWriteConcern(WriteConcern.WMajority);
    }

    private static MongoClient CreateClient(string connectionString, string databaseName, string key, string collectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
        return new MongoClient(connectionString);
    }
    private static string Required(IConfiguration configuration, string name) => configuration[$"Librarian:Mongo:{name}"] ??
        throw new InvalidOperationException($"Missing configuration: Librarian:Mongo:{name}");

    internal IQueryable<T> BuildQueryable<T>(string container)
    {
        var map = new BsonClassMap<MongoQueryDocument<T>>();
        map.MapMember(document => document.Body).SetElementName("body")
            .SetSerializer(MongoJsonSerializers.Create(typeof(T), LibrarianJson.Contract(typeof(T)).Options));
        map.SetIgnoreExtraElements(true);
        map.Freeze();
        return _collection.AsQueryable(new AggregateOptions
            { Collation = Collation.Simple, TranslationOptions = new ExpressionTranslationOptions { EnableClientSideProjections = false } })
            .Where(document => document["databaseKey"] == _key && document["container"] == container && document["body"] != BsonNull.Value)
            .As<BsonDocument, MongoQueryDocument<T>>(new BsonClassMapSerializer<MongoQueryDocument<T>>(map))
            .Select(document => document.Body);
    }

    internal async ValueTask Initialize(CancellationToken token)
    {
        Check(token);
        await _initialization.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await _collection.Indexes.CreateManyAsync([
                new CreateIndexModel<BsonDocument>(new BsonDocument { { "databaseKey", 1 }, { "container", 1 }, { "id", 1 } }, new CreateIndexOptions { Name = "librarian_address", Collation = Collation.Simple }),
                new CreateIndexModel<BsonDocument>(new BsonDocument("values.$**", 1), new CreateIndexOptions { Name = "librarian_values", Collation = Collation.Simple })
            ], token).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _initialization.Release(); }
    }

    public async ValueTask<MongoMetadata> ReadMetadata(CancellationToken token)
    {
        await Initialize(token).ConfigureAwait(false);
        BsonDocument? document = await _collection.Find(new BsonDocument("_id", MetadataId)).FirstOrDefaultAsync(token).ConfigureAwait(false);
        if (document is null)
        {
            var metadata = new MongoMetadata();
            try { await _collection.InsertOneAsync(Metadata(metadata), cancellationToken: token).ConfigureAwait(false); return metadata; }
            catch (MongoWriteException exception) when (exception.WriteError.Category == ServerErrorCategory.DuplicateKey)
            { document = await _collection.Find(new BsonDocument("_id", MetadataId)).FirstAsync(token).ConfigureAwait(false); }
        }
        return JsonSerializer.Deserialize(document["json"].AsString, MongoJsonContext.Default.MongoMetadata) ?? throw new InvalidOperationException("Invalid MongoDB Librarian metadata.");
    }
    private BsonDocument Metadata(MongoMetadata metadata) => new()
    {
        { "_id", MetadataId }, { "databaseKey", _key }, { "version", metadata.Version },
        { "json", JsonSerializer.Serialize(metadata, MongoJsonContext.Default.MongoMetadata) }
    };

    public async ValueTask<MongoDocument?> ReadDocument(string id, CancellationToken token)
    {
        await Initialize(token).ConfigureAwait(false);
        BsonDocument? result = await _collection.Find(new BsonDocument("_id", _key + "!" + id)).FirstOrDefaultAsync(token).ConfigureAwait(false);
        return result is null ? null : Decode(result);
    }
    private static MongoDocument Decode(BsonDocument document) =>
        JsonSerializer.Deserialize(document.ToJson(), MongoJsonContext.Default.MongoDocument) ?? throw new InvalidOperationException("Invalid MongoDB Librarian document.");

    private FilterDefinition<BsonDocument> Filter(MongoSelection selection)
    {
        var builder = Builders<BsonDocument>.Filter;
        FilterDefinition<BsonDocument> filter = builder.Eq("databaseKey", _key) & builder.Eq("container", selection.Container) & MongoQuery.Filter(selection.Filter);
        if (selection.Order is not null) filter &= builder.Exists("values." + MongoDocument.Field(selection.Order));
        return filter;
    }
    public async ValueTask<List<MongoDocument>> Select(MongoSelection selection, CancellationToken token)
    {
        await Initialize(token).ConfigureAwait(false);
        if (selection.Take == 0) return [];
        string order = selection.Order is null ? "id" : "values." + MongoDocument.Field(selection.Order);
        using IAsyncCursor<BsonDocument> cursor = await _collection.FindAsync(Filter(selection), new FindOptions<BsonDocument>
        {
            Collation = Collation.Simple, Sort = new BsonDocument(order, selection.Descending ? -1 : 1), Skip = selection.Skip,
            Limit = selection.Take == int.MaxValue ? null : selection.Take
        }, token).ConfigureAwait(false);
        var result = new List<MongoDocument>();
        while (await cursor.MoveNextAsync(token).ConfigureAwait(false))
            foreach (BsonDocument document in cursor.Current) result.Add(Decode(document));
        return result;
    }
    public async ValueTask<long> Count(MongoSelection selection, CancellationToken token)
    {
        await Initialize(token).ConfigureAwait(false);
        if (selection.Take == 0) return 0;
        return await _collection.CountDocumentsAsync(Filter(selection), new CountOptions
        { Collation = Collation.Simple, Skip = selection.Skip, Limit = selection.Take == int.MaxValue ? null : selection.Take }, token).ConfigureAwait(false);
    }

    public async ValueTask<bool> Commit(string expectedVersion, MongoMetadata metadata, IReadOnlyList<MongoMutation> writes, CancellationToken token)
    {
        await Initialize(token).ConfigureAwait(false);
        using IClientSessionHandle session = await _client.StartSessionAsync(cancellationToken: token).ConfigureAwait(false);
        session.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, ReadPreference.Primary, WriteConcern.WMajority));
        try
        {
            ReplaceOneResult replaced = await _collection.ReplaceOneAsync(session,
                new BsonDocument { { "_id", MetadataId }, { "version", expectedVersion } }, Metadata(metadata), cancellationToken: token).ConfigureAwait(false);
            if (replaced.MatchedCount == 0) { await session.AbortTransactionAsync(CancellationToken.None).ConfigureAwait(false); return false; }
            foreach (MongoMutation write in writes)
            {
                var filter = new BsonDocument("_id", _key + "!" + write.Id);
                if (write.Document is null) await _collection.DeleteOneAsync(session, filter, cancellationToken: token).ConfigureAwait(false);
                else
                {
                    BsonDocument document = BsonDocument.Parse(JsonSerializer.Serialize(write.Document, MongoJsonContext.Default.MongoDocument));
                    document["_id"] = _key + "!" + write.Id;
                    document["databaseKey"] = _key;
                    document["body"] = MongoJsonValue.Parse(write.Document.Json);
                    await _collection.ReplaceOneAsync(session, filter, document, new ReplaceOptions { IsUpsert = true }, token).ConfigureAwait(false);
                }
            }
            await session.CommitTransactionAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (MongoException exception) when (exception.HasErrorLabel("TransientTransactionError") && !exception.HasErrorLabel("UnknownTransactionCommitResult"))
        { return false; }
    }

    public void Check(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
    }

    internal async ValueTask<T> Consistent<T>(Func<CancellationToken, ValueTask<T>> read, CancellationToken token)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            Check(token);
            string version = (await ReadMetadata(token).ConfigureAwait(false)).Version;
            T result = await read(token).ConfigureAwait(false);
            if (version == (await ReadMetadata(token).ConfigureAwait(false)).Version) return result;
        }
        throw new TimeoutException("Could not read a consistent Librarian snapshot after 16 concurrent changes.");
    }

    internal async ValueTask<bool> Mutate(string container, string id, string? value, string mode, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (mode != "delete") ArgumentNullException.ThrowIfNull(value);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            Check(token);
            MongoMetadata metadata = await ReadMetadata(token).ConfigureAwait(false);
            string version = metadata.Version;
            MongoDocument? current = await ReadDocument(MongoDocument.Address(container, id), token).ConfigureAwait(false);
            if (mode == "add" && current is not null) return false;
            if (mode == "update" && current is null) return false;
            if (mode == "delete" && current is null) return true;
            MongoDocument? next = value is null ? null : MongoDocument.Create(container, current?.OriginalId ?? id, value, metadata.Paths(container));
            metadata.Version = Guid.NewGuid().ToString("N");
            if (await Commit(version, metadata, [new MongoMutation(MongoDocument.Address(container, id), next)], token).ConfigureAwait(false)) return true;
        }
        throw new TimeoutException("Librarian mutation exceeded 16 conflict retries.");
    }

    public async ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            Check(cancellationToken);
            MongoMetadata metadata = await ReadMetadata(cancellationToken).ConfigureAwait(false);
            string version = metadata.Version;
            bool satisfied = true;
            foreach (LibrarianCondition condition in batch.Conditions)
            {
                MongoDocument? current = await ReadDocument(MongoDocument.Address(condition.Container, condition.Id), cancellationToken).ConfigureAwait(false);
                if (!string.Equals(current?.Json, condition.ExpectedValue, StringComparison.Ordinal)) { satisfied = false; break; }
            }
            if (!satisfied)
            {
                if (version == (await ReadMetadata(cancellationToken).ConfigureAwait(false)).Version) return false;
                continue;
            }
            var writes = new List<MongoMutation>(batch.Writes.Count);
            foreach (LibrarianWrite write in batch.Writes)
            {
                string address = MongoDocument.Address(write.Container, write.Id);
                MongoDocument? current = await ReadDocument(address, cancellationToken).ConfigureAwait(false);
                if (write.Value is null && current is null) continue;
                writes.Add(new MongoMutation(address, write.Value is null ? null :
                    MongoDocument.Create(write.Container, current?.OriginalId ?? write.Id, write.Value, metadata.Paths(write.Container))));
            }
            metadata.Version = Guid.NewGuid().ToString("N");
            if (await Commit(version, metadata, writes, cancellationToken).ConfigureAwait(false)) return true;
        }
        throw new TimeoutException("Librarian batch exceeded 16 conflict retries.");
    }

    internal async ValueTask EnsureIndex(string container, string path, CancellationToken token)
    {
        MongoIndexValue.ValidatePath(path);
        for (int attempt = 0; attempt < 16; attempt++)
        {
            Check(token);
            MongoMetadata metadata = await ReadMetadata(token).ConfigureAwait(false);
            if (!metadata.Indexes.TryGetValue(container, out List<string>? paths)) metadata.Indexes.Add(container, paths = []);
            if (paths.Contains(path)) return;
            string version = metadata.Version;
            foreach (MongoDocument document in await Select(new MongoSelection(container, new MongoQueryFilter("all")), token).ConfigureAwait(false))
                MongoDocument.ValidateIndex(document.Json, path);
            paths.Add(path);
            metadata.Version = Guid.NewGuid().ToString("N");
            if (await Commit(version, metadata, [], token).ConfigureAwait(false)) return;
        }
        throw new TimeoutException("Librarian index registration exceeded 16 conflict retries.");
    }

    internal async ValueTask DeleteAll(string container, CancellationToken token)
    {
        for (int attempt = 0; attempt < 16; attempt++)
        {
            Check(token);
            MongoMetadata metadata = await ReadMetadata(token).ConfigureAwait(false);
            string version = metadata.Version;
            List<MongoDocument> documents = await Select(new MongoSelection(container, new MongoQueryFilter("all")), token).ConfigureAwait(false);
            var writes = new List<MongoMutation>(documents.Count);
            foreach (MongoDocument document in documents) writes.Add(new MongoMutation(document.Id, null));
            metadata.Version = Guid.NewGuid().ToString("N");
            if (await Commit(version, metadata, writes, token).ConfigureAwait(false)) return;
        }
        throw new TimeoutException("Librarian deletion exceeded 16 conflict retries.");
    }

    public async ValueTask<ILibrarianContainer> GetContainer(string containerName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Check(cancellationToken);
            if (!_containers.TryGetValue(containerName, out MongoLibrarianContainer? container))
                _containers.Add(containerName, container = new MongoLibrarianContainer(containerName, this));
            return container;
        }
        finally { _gate.Release(); }
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
            if (!_containers.Remove(containerName, out MongoLibrarianContainer? container)) return false;
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
            foreach (MongoLibrarianContainer container in _containers.Values) container.Dispose();
            _containers.Clear();
            if (_ownsClient) _client.Dispose();
        }
        finally { _gate.Release(); }
    }
}
