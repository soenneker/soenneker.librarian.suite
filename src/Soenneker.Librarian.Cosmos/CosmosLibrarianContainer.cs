using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Cosmos;

internal sealed partial class CosmosLibrarianContainer(CosmosLibrarianDatabase database, Container store, string name) : ILibrarianContainer
{
    private volatile bool _disposed;
    private void Check(CancellationToken token = default) { ObjectDisposedException.ThrowIf(_disposed, this); database.Check(token); }
    public void Dispose() => _disposed = true;
    private QueryRequestOptions QueryOptions => new() { PartitionKey = database.Partition };

    public IQueryable<T> BuildQueryable<T>()
    {
        Check();
        IQueryable<T> query = store.GetItemLinqQueryable<CosmosQueryDocument<T>>(allowSynchronousQueryExecution: true, requestOptions: QueryOptions)
            .Where(item => item.ContainerName == name && item.Body != null).Select(item => item.Body);
        return new CosmosQueryable<T>(query, () => Check());
    }

    public async ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        using MemoryStream content = CosmosDocument.Create(database.Key, name, id, document).ToStream();
        using ResponseMessage response = await store.CreateItemStreamAsync(content, database.Partition, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException($"Document '{id}' already exists.");
        response.EnsureSuccessStatusCode();
        return document;
    }
    private async ValueTask<(CosmosDocument? Document, string? ETag)> Read(string id, CancellationToken token)
    {
        Check(token);
        using ResponseMessage response = await store.ReadItemStreamAsync(CosmosDocument.IdFor(name, id), database.Partition, cancellationToken: token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return (null, null);
        response.EnsureSuccessStatusCode();
        using JsonDocument content = await JsonDocument.ParseAsync(response.Content, cancellationToken: token).ConfigureAwait(false);
        return (CosmosDocument.Read(content.RootElement), response.Headers.ETag);
    }
    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default) =>
        (await Read(id, cancellationToken).ConfigureAwait(false)).Document?.RawJson;
    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask<string?> UpdateItem(string id, string document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            (CosmosDocument? current, string? etag) = await Read(id, cancellationToken).ConfigureAwait(false);
            if (current is null) return null;
            using MemoryStream content = CosmosDocument.Create(database.Key, name, current.OriginalId, document).ToStream();
            using ResponseMessage response = await store.ReplaceItemStreamAsync(content, current.Id, database.Partition,
                new ItemRequestOptions { IfMatchEtag = etag }, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (response.StatusCode == HttpStatusCode.PreconditionFailed) continue;
            response.EnsureSuccessStatusCode();
            return document;
        }
        throw new TimeoutException("Cosmos document update exceeded five concurrency retries.");
    }
    public async ValueTask<string> UpdateItemStrict(string id, string document, CancellationToken cancellationToken = default) =>
        await UpdateItem(id, document, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");
    public async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        using ResponseMessage response = await store.DeleteItemStreamAsync(CosmosDocument.IdFor(name, id), database.Partition, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }

    public async ValueTask<string?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(ids);
        var addresses = new List<(string, PartitionKey)>(ids.Count);
        foreach (string id in ids) addresses.Add((CosmosDocument.IdFor(name, id), database.Partition));
        if (ids.Count == 0) return [];
        using ResponseMessage response = await store.ReadManyItemsStreamAsync(addresses.Distinct().ToList(), cancellationToken: cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using JsonDocument body = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement item in body.RootElement.GetProperty("Documents").EnumerateArray())
        {
            CosmosDocument document = CosmosDocument.Read(item);
            found.Add(document.Id, document.RawJson);
        }
        return addresses.Select(address => found.GetValueOrDefault(address.Item1)).ToArray();
    }

    private QueryDefinition Query(string select, string suffix = "") =>
        new QueryDefinition($"SELECT VALUE {select} FROM c WHERE c.containerName = @container {suffix}").WithParameter("@container", name);
    private async ValueTask<List<T>> Query<T>(QueryDefinition query, Func<JsonElement, T> materialize, CancellationToken token)
    {
        Check(token);
        using FeedIterator iterator = store.GetItemQueryStreamIterator(query, requestOptions: QueryOptions);
        var items = new List<T>();
        while (iterator.HasMoreResults)
        {
            using ResponseMessage response = await iterator.ReadNextAsync(token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument page = await JsonDocument.ParseAsync(response.Content, cancellationToken: token).ConfigureAwait(false);
            foreach (JsonElement value in page.RootElement.GetProperty("Documents").EnumerateArray()) items.Add(materialize(value));
        }
        return items;
    }
    public async ValueTask<int> CountItems(CancellationToken cancellationToken = default) =>
        (await Query(Query("COUNT(1)"), value => value.GetInt32(), cancellationToken).ConfigureAwait(false)).Single();
    public ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default) => Query(Query("c.rawJson"), value => value.GetString()!, cancellationToken);
    public ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default) => Query(Query("c.originalId"), value => value.GetString()!, cancellationToken);
    public ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default) =>
        Query(Query("{\"id\": c.originalId, \"value\": c.rawJson}"), value => new IdValuePair { Id = value.GetProperty("id").GetString()!, Value = value.GetProperty("value").GetString()! }, cancellationToken);
    public async ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    { Check(cancellationToken); await database.Clear(name, cancellationToken).ConfigureAwait(false); }
}
