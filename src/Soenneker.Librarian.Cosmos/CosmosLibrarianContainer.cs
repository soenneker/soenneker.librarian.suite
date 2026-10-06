using Soenneker.Atomics.ValueBools;
using Soenneker.Extensions.ValueTask;
using Soenneker.Extensions.Task;
using System;
using System.Buffers;
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

internal sealed partial class CosmosLibrarianContainer(CosmosLibrarianDatabase database, Container store, string name, string? partition) : ILibrarianContainer
{
    private ValueAtomicBool _disposed = new(false);
    private void Check(CancellationToken token = default) { ObjectDisposedException.ThrowIf(_disposed.Value, this); database.Check(token); }
    public void Dispose() => _disposed.TrySetTrue();
    private QueryRequestOptions QueryOptions => new() { PartitionKey = partition is null ? null : new PartitionKey(partition) };
    private (string Id, string Partition) Address(string id) => LibrarianDocumentJson.Address(id, partition);
    private MemoryStream Content(string id, string document)
    {
        LibrarianDocumentJson.Validate(id, document, partition);
        return new MemoryStream(Encoding.UTF8.GetBytes(document));
    }
    private static string Json(JsonElement document)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.EnumerateObject())
                if (!property.NameEquals("_rid") && !property.NameEquals("_self") && !property.NameEquals("_etag") &&
                    !property.NameEquals("_attachments") && !property.NameEquals("_ts")) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public IQueryable<T> BuildQueryable<T>()
    {
        Check();
        IQueryable<T> query = store.GetItemLinqQueryable<T>(allowSynchronousQueryExecution: true, requestOptions: QueryOptions);
        return new CosmosQueryable<T>(query, () => Check(), store, QueryOptions, JsonSerializer.Serialize(new[] { database.Key, name, partition }, CosmosInternalJsonContext.Default.StringArray));
    }

    public async ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        using MemoryStream content = Content(id, document);
        using ResponseMessage response = await store.CreateItemStreamAsync(content, new PartitionKey(Address(id).Partition), cancellationToken: cancellationToken).NoSync();
        if (response.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException($"Document '{id}' already exists.");
        response.EnsureSuccessStatusCode();
        return document;
    }
    private async ValueTask<(string? Document, string? ETag)> Read(string id, CancellationToken token)
    {
        Check(token);
        (string documentId, string partitionKey) = Address(id);
        using ResponseMessage response = await store.ReadItemStreamAsync(documentId, new PartitionKey(partitionKey), cancellationToken: token).NoSync();
        if (response.StatusCode == HttpStatusCode.NotFound) return (null, null);
        response.EnsureSuccessStatusCode();
        using JsonDocument content = await JsonDocument.ParseAsync(response.Content, cancellationToken: token).NoSync();
        return (Json(content.RootElement), response.Headers.ETag);
    }
    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default) =>
        (await Read(id, cancellationToken).NoSync()).Document;
    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).NoSync() ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask<string?> UpdateItem(string id, string document, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        using MemoryStream content = Content(id, document);
        (string documentId, string partitionKey) = Address(id);
        using ResponseMessage response = await store.ReplaceItemStreamAsync(content, documentId, new PartitionKey(partitionKey), cancellationToken: cancellationToken).NoSync();
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
        using ResponseMessage response = await store.DeleteItemStreamAsync(documentId, new PartitionKey(partitionKey), cancellationToken: cancellationToken).NoSync();
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }

    public async ValueTask<string?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(ids);
        (string Id, string Partition)[] addresses = ids.Select(Address).ToArray();
        if (ids.Count == 0) return [];
        using ResponseMessage response = await store.ReadManyItemsStreamAsync(addresses.Distinct().Select(a => (a.Id, new PartitionKey(a.Partition))).ToList(), cancellationToken: cancellationToken).NoSync();
        response.EnsureSuccessStatusCode();
        using JsonDocument body = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).NoSync();
        var found = new Dictionary<(string, string), string>();
        foreach (JsonElement item in body.RootElement.GetProperty("Documents").EnumerateArray())
            found.Add((item.GetProperty("id").GetString()!, item.GetProperty("partitionKey").GetString()!), Json(item));
        return addresses.Select(found.GetValueOrDefault).ToArray();
    }

    private static QueryDefinition Query(string select, string suffix = "") => new($"SELECT VALUE {select} FROM c WHERE true {suffix}");
    private async ValueTask<List<T>> Query<T>(QueryDefinition query, Func<JsonElement, T> materialize, CancellationToken token)
    {
        Check(token);
        using FeedIterator iterator = store.GetItemQueryStreamIterator(query, requestOptions: QueryOptions);
        var items = new List<T>();
        while (iterator.HasMoreResults)
        {
            using ResponseMessage response = await iterator.ReadNextAsync(token).NoSync();
            response.EnsureSuccessStatusCode();
            using JsonDocument page = await JsonDocument.ParseAsync(response.Content, cancellationToken: token).NoSync();
            foreach (JsonElement value in page.RootElement.GetProperty("Documents").EnumerateArray()) items.Add(materialize(value));
        }
        return items;
    }
    public async ValueTask<int> CountItems(CancellationToken cancellationToken = default) =>
        (await Query(Query("COUNT(1)"), value => value.GetInt32(), cancellationToken).NoSync()).Single();
    public ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default) => Query(Query("c"), Json, cancellationToken);
    public ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default) => Query(Query("{\"id\":c.id,\"partitionKey\":c.partitionKey}"), LibrarianDocumentJson.Id, cancellationToken);
    public ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default) =>
        Query(Query("c"), value => new IdValuePair { Id = LibrarianDocumentJson.Id(value), Value = Json(value) }, cancellationToken);
    public async ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        List<string> ids = await GetAllIds(cancellationToken).NoSync();
        foreach (string id in ids) await DeleteItem(id, cancellationToken).NoSync();
    }
}
