using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Cosmos;

internal sealed partial class CosmosLibrarianContainer
{
    public async ValueTask<LibrarianItem<string>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default)
    {
        (string? document, string? version) = await Read(id, cancellationToken).NoSync();
        return document is null ? null : new LibrarianItem<string>(document, version!);
    }
    public async ValueTask<LibrarianItem<string>?> UpdateItemIfVersion(string id, string document, string version, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        using MemoryStream content = Content(id, document);
        (string documentId, string partitionKey) = Address(id);
        using ResponseMessage response = await store.ReplaceItemStreamAsync(content, documentId, new PartitionKey(partitionKey),
            new ItemRequestOptions { IfMatchEtag = version }, cancellationToken).NoSync();
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed) return null;
        response.EnsureSuccessStatusCode();
        return new LibrarianItem<string>(document, response.Headers.ETag);
    }
    public async ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        (string documentId, string partitionKey) = Address(id);
        using ResponseMessage response = await store.DeleteItemStreamAsync(documentId, new PartitionKey(partitionKey),
            new ItemRequestOptions { IfMatchEtag = version }, cancellationToken).NoSync();
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }
}
