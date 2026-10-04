using Soenneker.Extensions.Task;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Librarian.Abstractions.Transactions;

namespace Soenneker.Librarian.Mongo;

public sealed partial class MongoLibrarianDatabase
{
    public ValueTask<bool> Execute(LibrarianBatch batch, CancellationToken cancellationToken = default) => ExecutePartition(batch, null, cancellationToken);
    public ValueTask<bool> Execute(LibrarianBatch batch, string partitionKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        return ExecutePartition(batch, partitionKey, cancellationToken);
    }
    private async ValueTask<bool> ExecutePartition(LibrarianBatch batch, string? partition, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Check(token);
        batch.ValidateConcurrency(supportsVersions: true);
        if (batch.Writes.Count == 0) return true;
        var addresses = new HashSet<(string, string)>();
        foreach (LibrarianWrite write in batch.Writes)
        {
            if (!addresses.Add((write.Container, MongoLibrarianContainer.Identity(write.Id, partition))))
                throw new ArgumentException("Duplicate document identity in the batch.", nameof(batch));
            if (write.Value is not null) LibrarianDocumentJson.Validate(write.Id, write.Value, partition);
        }
        using IClientSessionHandle session = await _client.StartSessionAsync(cancellationToken: token).NoSync();
        session.StartTransaction(new TransactionOptions(ReadConcern.Snapshot, ReadPreference.Primary, WriteConcern.WMajority));
        bool committing = false;
        try
        {
            foreach (LibrarianWrite write in batch.Writes)
            {
                IMongoCollection<BsonDocument> collection = Collection(write.Container);
                var filter = new BsonDocument("_id", MongoLibrarianContainer.Identity(write.Id, partition));
                if (write.ExpectedVersion is not null) filter.Add("_librarianVersion", write.ExpectedVersion);
                bool applied;
                if (write.Value is null)
                    applied = (await collection.DeleteOneAsync(session, filter, cancellationToken: token).NoSync()).DeletedCount != 0;
                else if (write.CreateOnly)
                {
                    await collection.InsertOneAsync(session, MongoLibrarianContainer.Encode(write.Id, write.Value, partition), cancellationToken: token).NoSync();
                    applied = true;
                }
                else
                {
                    ReplaceOneResult result = await collection.ReplaceOneAsync(session, filter, MongoLibrarianContainer.Encode(write.Id, write.Value, partition),
                        new ReplaceOptions { IsUpsert = write.ExpectedVersion is null }, token).NoSync();
                    applied = result.MatchedCount != 0 || result.UpsertedId is not null;
                }
                if (!applied) { await session.AbortTransactionAsync(token).NoSync(); return false; }
            }
            committing = true;
            await session.CommitTransactionAsync(token).NoSync();
            return true;
        }
        catch (MongoException exception) when (!committing && (exception.HasErrorLabel("TransientTransactionError") ||
            exception is MongoWriteException { WriteError.Category: ServerErrorCategory.DuplicateKey }))
        {
            if (session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None).NoSync();
            return false;
        }
        catch
        {
            if (!committing && session.IsInTransaction) await session.AbortTransactionAsync(CancellationToken.None).NoSync();
            throw;
        }
    }
}
