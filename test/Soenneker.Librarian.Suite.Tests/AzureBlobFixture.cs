using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class AzureBlobFixture
{
    public string? Snapshot { get; set; }
    public int Revision { get; private set; }
    public int Writes { get; private set; }
    public string? ReadError { get; set; }
    public bool FailWrites { get; set; }
    public Mock<BlobClient> Client { get; } = new();

    public AzureBlobFixture()
    {
        Client.Setup(client => client.DownloadContentAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) =>
            {
                token.ThrowIfCancellationRequested();
                if (ReadError is not null) throw new RequestFailedException(404, "Read failed", ReadError, null);
                if (Snapshot is null) throw new RequestFailedException(404, "Missing", "BlobNotFound", null);
                return Task.FromResult(Response.FromValue(
                    BlobsModelFactory.BlobDownloadResult(BinaryData.FromString(Snapshot),
                        BlobsModelFactory.BlobDownloadDetails(eTag: new ETag(Revision.ToString()))),
                    Mock.Of<Response>()));
            });
        Client.Setup(client => client.UploadAsync(It.IsAny<BinaryData>(), It.IsAny<BlobUploadOptions>(), It.IsAny<CancellationToken>()))
            .Returns((BinaryData data, BlobUploadOptions options, CancellationToken token) =>
            {
                token.ThrowIfCancellationRequested();
                if (FailWrites) throw new RequestFailedException(503, "Unavailable");
                if (Snapshot is null ? options.Conditions.IfNoneMatch != ETag.All :
                    options.Conditions.IfMatch != new ETag(Revision.ToString()))
                    throw new RequestFailedException(412, "Conflict", "ConditionNotMet", null);
                if (options.HttpHeaders.ContentType != "application/json; charset=utf-8")
                    throw new Exception("Missing JSON content type.");
                Snapshot = data.ToString();
                Writes++;
                Revision++;
                return Task.FromResult(Response.FromValue(
                    BlobsModelFactory.BlobContentInfo(new ETag(Revision.ToString()), DateTimeOffset.UtcNow, null, null, null),
                    Mock.Of<Response>()));
            });
    }

    public AzureBlob.AzureBlobLibrarianDatabase Open() => new(Client.Object, NullLogger.Instance);
}
