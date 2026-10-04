using Soenneker.Extensions.Task;
using System;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Mongo;

internal sealed partial class MongoLibrarianContainer
{
    public async ValueTask<LibrarianItem<string>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        BsonDocument? document = await Store.Find(Filter(id)).FirstOrDefaultAsync(cancellationToken).NoSync();
        return document is null ? null : new LibrarianItem<string>(Json(document), document["_librarianVersion"].AsString);
    }
    public async ValueTask<LibrarianItem<string>?> UpdateItemIfVersion(string id, string document, string version, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        BsonDocument next = Encode(id, document, partition);
        ReplaceOneResult result = await Store.ReplaceOneAsync(Filter(id) & Builders<BsonDocument>.Filter.Eq("_librarianVersion", version), next, cancellationToken: cancellationToken).NoSync();
        return result.MatchedCount == 0 ? null : new LibrarianItem<string>(document, next["_librarianVersion"].AsString);
    }
    public async ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return (await Store.DeleteOneAsync(Filter(id) & Builders<BsonDocument>.Filter.Eq("_librarianVersion", version), cancellationToken).NoSync()).DeletedCount != 0;
    }
}
