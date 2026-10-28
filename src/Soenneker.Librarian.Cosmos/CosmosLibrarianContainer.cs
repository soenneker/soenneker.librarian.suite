using Soenneker.Atomics.ValueBools;
using Soenneker.Extensions.ValueTask;
using Soenneker.Extensions.Task;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Cosmos;

internal sealed partial class CosmosLibrarianContainer(CosmosLibrarianDatabase database, Container store, string name, string? partition) : ICosmosLibrarianContainer
{
    internal ICosmosLibrarianContainer WithOptions(CosmosLibrarianOptions options, string? selectedPartition) =>
        new CosmosLibrarianContainer(new CosmosLibrarianDatabase(database, options), store, name, selectedPartition);

    private ValueAtomicBool _disposed = new(false);
    private void Check(CancellationToken token = default) { ObjectDisposedException.ThrowIf(_disposed.Value, this); database.Check(token); }
    public void Dispose() => _disposed.TrySetTrue();
    private QueryRequestOptions QueryOptions => database.QueryOptions(name, partition);
    private (string Id, string Partition) Address(string id) => LibrarianDocumentJson.Address(id, partition);
    private MemoryStream Content(string id, string document)
    {
        LibrarianDocumentJson.Validate(id, document, partition);
        return new CosmosUtf8Stream(document);
    }
    private static string Json(JsonElement document)
    {
        bool hasMetadata = false;
        foreach (JsonProperty property in document.EnumerateObject())
        {
            if (!property.NameEquals("_rid") && !property.NameEquals("_self") && !property.NameEquals("_etag") &&
                !property.NameEquals("_attachments") && !property.NameEquals("_ts")) continue;
            hasMetadata = true;
            break;
        }
        if (!hasMetadata) return document.GetRawText();
        using var buffer = new CosmosJsonBuffer();
        WriteDocument(buffer, document);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public IQueryable<T> BuildQueryable<T>()
    {
        Check();
        IQueryable<T> query = store.GetItemLinqQueryable<T>(allowSynchronousQueryExecution: false, requestOptions: QueryOptions);
        return new CosmosQueryable<T>(query, () => Check(), store, database, name, partition);
    }

    public async ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        using MemoryStream content = Content(id, document);
        using ResponseMessage response = await store.CreateItemStreamAsync(content, new PartitionKey(Address(id).Partition), database.ItemOptions(name, nameof(AddItem)), cancellationToken: cancellationToken).NoSync();
        database.Observe(name, "AddItem", response.StatusCode, response.Headers, response.Diagnostics);
        if (response.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException($"Document '{id}' already exists.");
        response.EnsureSuccessStatusCode();
        return document;
    }
    private async ValueTask<(string? Document, string? ETag)> Read(string id, CancellationToken token)
    {
        Check(token);
        (string documentId, string partitionKey) = Address(id);
        using ResponseMessage response = await store.ReadItemStreamAsync(documentId, new PartitionKey(partitionKey), database.ItemOptions(name, nameof(GetItem)), cancellationToken: token).NoSync();
        database.Observe(name, "GetItem", response.StatusCode, response.Headers, response.Diagnostics);
        if (response.StatusCode == HttpStatusCode.NotFound) return (null, null);
        response.EnsureSuccessStatusCode();
        using JsonDocument content = await JsonDocument.ParseAsync(response.Content, cancellationToken: token).NoSync();
        return (Json(content.RootElement), response.Headers.ETag);
    }
    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default) =>
        (await Read(id, cancellationToken).NoSync()).Document;
    public async ValueTask<bool> Exists(string id, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        (string documentId, string partitionKey) = Address(id);
        using ResponseMessage response = await store.ReadItemStreamAsync(documentId, new PartitionKey(partitionKey),
            database.ItemOptions(name, nameof(Exists)), cancellationToken).NoSync();
        database.Observe(name, nameof(Exists), response.StatusCode, response.Headers, response.Diagnostics);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).NoSync() ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask<string?> UpdateItem(string id, string document, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        using MemoryStream content = Content(id, document);
        (string documentId, string partitionKey) = Address(id);
        using ResponseMessage response = await store.ReplaceItemStreamAsync(content, documentId, new PartitionKey(partitionKey), database.ItemOptions(name, nameof(UpdateItem)), cancellationToken: cancellationToken).NoSync();
        database.Observe(name, "UpdateItem", response.StatusCode, response.Headers, response.Diagnostics);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return document;
    }
    public async ValueTask<string> UpdateItemStrict(string id, string document, CancellationToken cancellationToken = default) =>
        await UpdateItem(id, document, cancellationToken).NoSync() ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");
    public async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        (string documentId, string partitionKey) = Address(id);
        using ResponseMessage response = await store.DeleteItemStreamAsync(documentId, new PartitionKey(partitionKey), database.ItemOptions(name, nameof(DeleteItem)), cancellationToken: cancellationToken).NoSync();
        database.Observe(name, "DeleteItem", response.StatusCode, response.Headers, response.Diagnostics);
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }

    public ValueTask<string?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) =>
        ReadMany(ids, Json, cancellationToken);

    private async ValueTask<T?[]> ReadMany<T>(IReadOnlyList<string> ids, Func<JsonElement, T> materialize,
        CancellationToken cancellationToken) where T : class
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return [];
        var addresses = new (string Id, string Partition)[ids.Count];
        var unique = new Dictionary<(string, string), JsonElement>(ids.Count);
        var request = new List<(string, PartitionKey)>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            var address = Address(ids[i]);
            addresses[i] = address;
            if (unique.TryAdd(address, default)) request.Add((address.Id, new PartitionKey(address.Partition)));
        }
        using ResponseMessage response = await store.ReadManyItemsStreamAsync(request,
            database.Options.ReadManyRequestOptionsFactory?.Invoke(name), cancellationToken: cancellationToken).NoSync();
        database.Observe(name, "GetItems", response.StatusCode, response.Headers, response.Diagnostics);
        response.EnsureSuccessStatusCode();
        using JsonDocument body = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).NoSync();
        foreach (JsonElement item in body.RootElement.GetProperty("Documents").EnumerateArray())
            unique[(item.GetProperty("id").GetString()!, item.GetProperty("partitionKey").GetString()!)] = item;
        var result = new T?[addresses.Length];
        // Materialize each occurrence separately so duplicate IDs never alias mutable document instances.
        for (int i = 0; i < addresses.Length; i++)
        {
            JsonElement item = unique[addresses[i]];
            if (item.ValueKind != JsonValueKind.Undefined) result[i] = materialize(item);
        }
        return result;
    }

    private static QueryDefinition Query(string select, string suffix = "") => new($"SELECT VALUE {select} FROM c WHERE true {suffix}");
    private async ValueTask<List<T>> Query<T>(QueryDefinition query, Func<JsonElement, T> materialize, CancellationToken token)
    {
        Check(token);
        using FeedIterator iterator = store.GetItemQueryStreamIterator(query, requestOptions: QueryOptions);
        var items = new List<T>();
        while (iterator.HasMoreResults)
        {
            token.ThrowIfCancellationRequested();
            using ResponseMessage response = await iterator.ReadNextAsync(token).NoSync();
            database.Observe(name, "Query", response.StatusCode, response.Headers, response.Diagnostics);
            response.EnsureSuccessStatusCode();
            using JsonDocument page = await JsonDocument.ParseAsync(response.Content, cancellationToken: token).NoSync();
            JsonElement documents = page.RootElement.GetProperty("Documents");
            items.EnsureCapacity(items.Count + documents.GetArrayLength());
            foreach (JsonElement value in documents.EnumerateArray()) items.Add(materialize(value));
        }
        return items;
    }
    private async ValueTask<int> QueryScalar(QueryDefinition query, bool existence, CancellationToken token)
    {
        Check(token);
        QueryRequestOptions options = QueryOptions;
        if (existence) options.MaxItemCount = 1;
        using FeedIterator iterator = store.GetItemQueryStreamIterator(query, requestOptions: options);
        bool found = false;
        int result = 0;
        while (iterator.HasMoreResults)
        {
            token.ThrowIfCancellationRequested();
            using ResponseMessage response = await iterator.ReadNextAsync(token).NoSync();
            database.Observe(name, "Query", response.StatusCode, response.Headers, response.Diagnostics);
            response.EnsureSuccessStatusCode();
            using JsonDocument page = await JsonDocument.ParseAsync(response.Content, cancellationToken: token).NoSync();
            foreach (JsonElement value in page.RootElement.GetProperty("Documents").EnumerateArray())
            {
                if (existence) return 1;
                if (found) throw new InvalidOperationException("Sequence contains more than one element");
                result = value.GetInt32();
                found = true;
            }
        }
        if (!found && !existence) throw new InvalidOperationException("Sequence contains no elements");
        return result;
    }
    public ValueTask<int> CountItems(CancellationToken cancellationToken = default) =>
        QueryScalar(Query("COUNT(1)"), false, cancellationToken);
    public ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default) => Query(Query("c"), Json, cancellationToken);
    public ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default) => Query(Query("{\"id\":c.id,\"partitionKey\":c.partitionKey}"), LibrarianDocumentJson.Id, cancellationToken);
    public ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default) =>
        Query(Query("c"), value => new IdValuePair { Id = LibrarianDocumentJson.Id(value), Value = Json(value) }, cancellationToken);
    public async ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        QueryRequestOptions options = QueryOptions;
        if (options.MaxItemCount is null or <= 0) options.MaxItemCount = 100;
        using FeedIterator iterator = store.GetItemQueryStreamIterator(Query("{\"id\":c.id,\"partitionKey\":c.partitionKey}"), requestOptions: options);
        while (iterator.HasMoreResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using ResponseMessage response = await iterator.ReadNextAsync(cancellationToken).NoSync();
            database.Observe(name, "Query", response.StatusCode, response.Headers, response.Diagnostics);
            response.EnsureSuccessStatusCode();
            using JsonDocument page = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).NoSync();
            foreach (JsonElement document in page.RootElement.GetProperty("Documents").EnumerateArray())
                await DeleteItem(LibrarianDocumentJson.Id(document), cancellationToken).NoSync();
        }
    }
}
