using Soenneker.Atomics.ValueBools;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Mongo;

internal sealed partial class MongoLibrarianContainer(string name, MongoLibrarianDatabase database, string? partition)
    : ILibrarianContainer
{
    private ValueAtomicBool _disposed = new(false);
    private IMongoCollection<BsonDocument> Store => database.Collection(name);

    private void Check(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        database.Check(token);
    }

    public void Dispose() => _disposed.TrySetTrue();

    private FilterDefinition<BsonDocument> Scope => partition is null
        ? FilterDefinition<BsonDocument>.Empty
        : Builders<BsonDocument>.Filter.Eq("partitionKey", partition);

    internal static string Identity(string id, string? partition = null)
    {
        (string Id, string Partition) address = LibrarianDocumentJson.Address(id, partition);
        return address.Partition == address.Id ? address.Id : address.Partition + ":" + address.Id;
    }

    private FilterDefinition<BsonDocument> Filter(string id) =>
        Builders<BsonDocument>.Filter.Eq("_id", Identity(id, partition));

    internal static BsonDocument Encode(string id, string json, string? partition = null)
    {
        BsonDocument document =
            MongoJsonValue.FromJson(LibrarianDocumentJson.Parse(id, json, partition)).AsBsonDocument;
        document["_id"] = Identity(id, partition);
        document["_librarianVersion"] = Guid.NewGuid().ToString("N");
        return document;
    }

    internal static string Json(BsonDocument document) => MongoJsonValue.ToJson(document, excludeMetadata: true);

    public IQueryable<T> BuildQueryable<T>()
    {
        Check();
        return new MongoQueryable<T>(database.BuildQueryable<T>(name, partition), Check);
    }

    public async ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        try
        {
            await Store.InsertOneAsync(Encode(id, document, partition), cancellationToken: cancellationToken)
                       .NoSync();
        }
        catch (MongoWriteException e) when (e.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new InvalidOperationException($"Document '{id}' already exists.", e);
        }

        return document;
    }

    public async ValueTask<string?> UpdateItem(string id, string document,
        CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        BsonDocument next = Encode(id, document, partition);
        ReplaceOneResult result = await Store.ReplaceOneAsync(Filter(id), next, cancellationToken: cancellationToken)
                                             .NoSync();
        return result.MatchedCount == 0 ? null : document;
    }

    public async ValueTask<string> UpdateItemStrict(string id, string document,
        CancellationToken cancellationToken = default) =>
        await UpdateItem(id, document, cancellationToken).NoSync() ??
        throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        await Store.DeleteOneAsync(Filter(id), cancellationToken).NoSync();
    }

    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        BsonDocument? value = await Store.Find(Filter(id)).FirstOrDefaultAsync(cancellationToken).NoSync();
        return value is null ? null : Json(value);
    }

    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).NoSync() ??
        throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask<string?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(ids);
        string[] addresses = ids.Select(id => Identity(id, partition)).ToArray();
        if (addresses.Length == 0)
            return [];
        List<BsonDocument> values = await Store.Find(Builders<BsonDocument>.Filter.In("_id", addresses))
                                               .ToListAsync(cancellationToken).NoSync();
        Dictionary<string, string> found =
            values.ToDictionary(value => value["_id"].AsString, Json, StringComparer.Ordinal);
        return addresses.Select(address => found.GetValueOrDefault(address)).ToArray();
    }

    public async ValueTask<int> CountItems(CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        return checked((int)await Store.CountDocumentsAsync(Scope, cancellationToken: cancellationToken)
                                       .NoSync());
    }

    private Task<List<BsonDocument>> All(CancellationToken token)
    {
        Check(token);
        return Store.Find(Scope).ToListAsync(token);
    }

    public async ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default) =>
        (await All(cancellationToken).NoSync()).Select(Json).ToList();

    public async ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        using IAsyncCursor<BsonDocument> cursor = await Store.Find(Scope)
            .Project(Builders<BsonDocument>.Projection.Include("_id")).ToCursorAsync(cancellationToken).NoSync();
        var ids = new List<string>();
        while (await cursor.MoveNextAsync(cancellationToken).NoSync())
            foreach (BsonDocument document in cursor.Current) ids.Add(document["_id"].AsString);
        return ids;
    }

    public async ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default) =>
        (await All(cancellationToken).NoSync())
        .Select(value => new IdValuePair { Id = value["_id"].AsString, Value = Json(value) }).ToList();

    public async ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        await Store.DeleteManyAsync(Scope, cancellationToken).NoSync();
    }
}
