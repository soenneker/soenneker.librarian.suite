using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Documents.Document;
using Soenneker.Extensions.Task;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Cosmos;

internal sealed partial class CosmosLibrarianContainer
{
    private static T ApplyVersion<T>(T document, string version)
    {
        if (document is ICosmosVersionedDocument versioned) versioned.ETag = version;
        return document;
    }

    public async ValueTask<T?[]> ReadDocuments<T>(IReadOnlyList<string> ids, CancellationToken cancellationToken = default) where T : Document
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0) return [];
        var addresses = new (string Id, string Partition)[ids.Count];
        var found = new Dictionary<(string, string), (T? Document, bool Returned)>(ids.Count);
        var request = new List<(string, PartitionKey)>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            var address = Address(ids[i]);
            addresses[i] = address;
            if (found.TryAdd(address, default)) request.Add((address.Id, new PartitionKey(address.Partition)));
        }
        FeedResponse<T> response = await store.ReadManyItemsAsync<T>(request,
            database.Options.ReadManyRequestOptionsFactory?.Invoke(name), cancellationToken).NoSync();
        database.Observe(name, nameof(GetItems), response.StatusCode, response.Headers, response.Diagnostics);
        foreach (T document in response)
            found[(document.DocumentId!, document.PartitionKey!)] = (document, false);
        var result = new T?[ids.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var entry = found[addresses[i]];
            if (entry.Document is null) continue;
            if (!entry.Returned)
            {
                result[i] = entry.Document;
                found[addresses[i]] = (entry.Document, true);
            }
            else
            {
                // Only duplicates need a copy; never return aliases of mutable documents.
                using Stream stream = Serializer.ToStream(entry.Document);
                result[i] = Serializer.FromStream<T>(stream);
            }
        }
        return result;
    }

    public async ValueTask<LibrarianItem<T>?> ReadDocument<T>(string id, CancellationToken cancellationToken = default) where T : Document
    {
        Check(cancellationToken);
        var address = Address(id);
        using ResponseMessage response = await store.ReadItemStreamAsync(address.Id, new PartitionKey(address.Partition),
            database.ItemOptions(name, nameof(GetItem)), cancellationToken).NoSync();
        database.Observe(name, nameof(GetItem), response.StatusCode, response.Headers, response.Diagnostics);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return new LibrarianItem<T>(ApplyVersion(Serializer.FromStream<T>(response.Content), response.Headers.ETag), response.Headers.ETag);
    }

    private (string Id, string Partition) ValidateDocument<T>(T document) where T : Document
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.DocumentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(document.PartitionKey);
        var address = Address(document.Id!);
        if (address.Id != document.DocumentId || address.Partition != document.PartitionKey)
            throw new ArgumentException("Document identity does not match its container partition.", nameof(document));
        return address;
    }

    public async ValueTask CreateDocument<T>(T document, CancellationToken cancellationToken = default) where T : Document
    {
        Check(cancellationToken);
        var address = ValidateDocument(document);
        using Stream content = Serializer.ToStream(document);
        using ResponseMessage response = await store.CreateItemStreamAsync(content, new PartitionKey(address.Partition),
            database.ItemOptions(name, nameof(AddItem)), cancellationToken).NoSync();
        database.Observe(name, nameof(AddItem), response.StatusCode, response.Headers, response.Diagnostics);
        response.EnsureSuccessStatusCode();
        ApplyVersion(document, response.Headers.ETag);
    }

    public async ValueTask<LibrarianItem<T>?> ReplaceDocument<T>(T document, string? version = null,
        CancellationToken cancellationToken = default) where T : Document
    {
        Check(cancellationToken);
        if (version is not null) ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var address = ValidateDocument(document);
        using Stream content = Serializer.ToStream(document);
        using ResponseMessage response = await store.ReplaceItemStreamAsync(content, address.Id, new PartitionKey(address.Partition),
            database.ItemOptions(name, nameof(UpdateItem), version), cancellationToken).NoSync();
        database.Observe(name, nameof(UpdateItem), response.StatusCode, response.Headers, response.Diagnostics);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed) return null;
        response.EnsureSuccessStatusCode();
        return new LibrarianItem<T>(ApplyVersion(document, response.Headers.ETag), response.Headers.ETag);
    }
}
