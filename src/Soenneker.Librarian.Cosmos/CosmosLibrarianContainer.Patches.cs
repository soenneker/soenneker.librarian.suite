using System;
using System.Collections.Generic;
using System.Net;
using System.IO;
using Soenneker.Documents.Document;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Extensions.Task;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Cosmos;

internal sealed partial class CosmosLibrarianContainer
{
    public ValueTask<LibrarianItem<string>?> PatchItem(string id, IReadOnlyList<PatchOperation> operations,
        string? expectedVersion = null, CancellationToken cancellationToken = default) =>
        PatchCore(id, operations, expectedVersion, static stream =>
        {
            using JsonDocument body = JsonDocument.Parse(stream);
            return Json(body.RootElement);
        }, cancellationToken);

    public ValueTask<LibrarianItem<T>?> PatchDocument<T>(string id, IReadOnlyList<PatchOperation> operations,
        string? expectedVersion = null, CancellationToken cancellationToken = default) where T : Document =>
        PatchCore(id, operations, expectedVersion, stream => Serializer.FromStream<T>(stream), cancellationToken);

    private async ValueTask<LibrarianItem<T>?> PatchCore<T>(string id, IReadOnlyList<PatchOperation> operations,
        string? expectedVersion, Func<Stream, T> deserialize, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count is < 1 or > 10) throw new ArgumentException("Cosmos patches require between one and ten operations.", nameof(operations));
        if (expectedVersion is not null) ArgumentException.ThrowIfNullOrWhiteSpace(expectedVersion);
        foreach (PatchOperation operation in operations)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ValidatePatchPath(operation.Path);
            if (operation.OperationType == PatchOperationType.Move)
                throw new NotSupportedException("Move patches are not supported because they can change protected source paths.");
        }
        (string documentId, string partitionKey) = Address(id);
        PatchItemRequestOptions options = database.Options.PatchRequestOptionsFactory?.Invoke(name) ?? new PatchItemRequestOptions();
        // Cosmos Patch does not guarantee IfMatchEtag support; enforce the version in its atomic filter predicate.
        // https://learn.microsoft.com/dotnet/api/microsoft.azure.cosmos.container.patchitemasync
        string? version = expectedVersion ?? options.IfMatchEtag;
        if (version is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
            if (!string.IsNullOrWhiteSpace(options.FilterPredicate))
                throw new ArgumentException("Use either an expected patch version or a custom filter predicate, not both.");
            options.FilterPredicate = "FROM c WHERE c._etag = " + JsonSerializer.Serialize(version, CosmosInternalJsonContext.Default.String);
        }
        options.IfMatchEtag = null;
        if (options.IfNoneMatchEtag is not null) throw new NotSupportedException("Patch does not support IfNoneMatchEtag.");
        options.EnableContentResponseOnWrite = true;
        using ResponseMessage response = await store.PatchItemStreamAsync(documentId, new PartitionKey(partitionKey), operations,
            options, cancellationToken).NoSync();
        database.Observe(name, nameof(PatchItem), response.StatusCode, response.Headers, response.Diagnostics);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed) return null;
        response.EnsureSuccessStatusCode();
        return new LibrarianItem<T>(deserialize(response.Content), response.Headers.ETag);
    }

    private static void ValidatePatchPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path == "/")
            throw new ArgumentException("A patch must target a document field.", nameof(path));
        int end = path.IndexOf('/', 1);
        string field = path[1..(end < 0 ? path.Length : end)].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        if (field is "id" or "partitionKey" or "_id" or "_librarianVersion" or "_etag" or "_rid" or "_self" or "_attachments" or "_ts")
            throw new ArgumentException("Identity and provider metadata cannot be patched.", nameof(path));
    }
}
