using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Extensions.Task;
using Soenneker.Librarian.Core;

namespace Soenneker.Librarian.AzureBlob;

public sealed class AzureBlobLibrarianDatabase : SnapshotLibrarianDatabase, IAzureBlobLibrarianDatabase
{
    private readonly BlobClient _blob;
    private ETag? _etag;

    public AzureBlobLibrarianDatabase(IConfiguration configuration, ILogger<AzureBlobLibrarianDatabase> logger)
        : this(new BlobClient(Required(configuration, "ConnectionString"), Required(configuration, "ContainerName"),
            configuration["Librarian:AzureBlob:BlobName"] ?? "librarian.json"), logger) { }

    public AzureBlobLibrarianDatabase(BlobClient blob, ILogger logger) : base(logger)
    {
        ArgumentNullException.ThrowIfNull(blob);
        _blob = blob;
    }

    private static string Required(IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? value = configuration[$"Librarian:AzureBlob:{key}"];
        return !string.IsNullOrWhiteSpace(value) ? value :
            throw new InvalidOperationException($"Missing configuration: Librarian:AzureBlob:{key}");
    }

    protected override async ValueTask<string?> ReadSnapshot(CancellationToken cancellationToken)
    {
        try
        {
            Response<BlobDownloadResult> response = await _blob.DownloadContentAsync(cancellationToken).NoSync();
            string json = new UTF8Encoding(false, true).GetString(response.Value.Content.ToMemory().Span);
            _etag = response.Value.Details.ETag;
            return json;
        }
        catch (RequestFailedException exception) when (exception.Status == 404 &&
                                                       exception.ErrorCode == BlobErrorCode.BlobNotFound.ToString())
        {
            _etag = null;
            return null;
        }
    }

    protected override async ValueTask WriteSnapshot(string json, CancellationToken cancellationToken)
    {
        var options = new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json; charset=utf-8" },
            Conditions = _etag is { } etag
                ? new BlobRequestConditions { IfMatch = etag }
                : new BlobRequestConditions { IfNoneMatch = ETag.All }
        };
        Response<BlobContentInfo> response = await _blob.UploadAsync(BinaryData.FromString(json), options, cancellationToken).NoSync();
        _etag = response.Value.ETag;
    }

    public ValueTask DiscardAndDispose() => DisposeWithoutSaving();
}
