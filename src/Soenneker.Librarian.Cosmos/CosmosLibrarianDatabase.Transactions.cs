using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Librarian.Abstractions.Transactions;

namespace Soenneker.Librarian.Cosmos;

public sealed partial class CosmosLibrarianDatabase
{
    public ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default) =>
        ExecutePartition(batch, null, cancellationToken);

    public ValueTask<bool> Execute(LibrarianBatch batch, string partitionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        return ExecutePartition(batch, partitionKey, cancellationToken);
    }

    private async ValueTask<bool> ExecutePartition(LibrarianBatch batch, string? partition, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Check(token);
        batch.ValidateConcurrency(supportsVersions: true);
        if (batch.Writes.Count == 0)
            return true;
        if (batch.Writes.Count > 100)
            throw new ArgumentException("Cosmos transactions support at most 100 writes.", nameof(batch));
        string name = batch.Writes[0].Container;
        string selectedPartition = LibrarianDocumentJson.Address(batch.Writes[0].Id, partition).Partition;
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (LibrarianWrite write in batch.Writes)
        {
            (string Id, string Partition) address = LibrarianDocumentJson.Address(write.Id, partition);
            if (write.Container != name || address.Partition != selectedPartition)
                throw new NotSupportedException(
                    "Cosmos transactions require one container and one Document.PartitionKey.");
            if (!addresses.Add(address.Id))
                throw new ArgumentException("Duplicate document identity in the batch.", nameof(batch));
            if (write.Value is not null)
                LibrarianDocumentJson.Validate(write.Id, write.Value, partition);
        }

        Container store;
        using (await _gate.Acquire(token).NoSync())
        {
            store = await Store(name, token).NoSync();
        }

        TransactionalBatch transaction = store.CreateTransactionalBatch(new PartitionKey(selectedPartition));
        var streams = new List<MemoryStream>(batch.Writes.Count);
        try
        {
            foreach (LibrarianWrite write in batch.Writes)
            {
                string id = LibrarianDocumentJson.Address(write.Id, partition).Id;
                var options = new TransactionalBatchItemRequestOptions { IfMatchEtag = write.ExpectedVersion };
                if (write.Value is null)
                    transaction.DeleteItem(id, options);
                else
                {
                    var stream = new MemoryStream(Encoding.UTF8.GetBytes(write.Value));
                    streams.Add(stream);
                    if (write.CreateOnly)
                        transaction.CreateItemStream(stream);
                    else if (write.ExpectedVersion is not null)
                        transaction.ReplaceItemStream(id, stream, options);
                    else
                        transaction.UpsertItemStream(stream);
                }
            }

            using TransactionalBatchResponse response = await transaction.ExecuteAsync(token).NoSync();
            if (response.IsSuccessStatusCode)
                return true;
            if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed
                or HttpStatusCode.NotFound)
                return false;
            throw new CosmosException(response.ErrorMessage, response.StatusCode, 0, response.ActivityId,
                response.RequestCharge);
        }
        finally
        {
            foreach (MemoryStream stream in streams)
                stream.Dispose();
        }
    }
}
