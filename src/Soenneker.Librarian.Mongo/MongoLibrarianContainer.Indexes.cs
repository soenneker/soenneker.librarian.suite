using Soenneker.Extensions.Task;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Mongo;

internal sealed partial class MongoLibrarianContainer
{
    private static string Path(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Split('.').Any(segment => segment.Length == 0 || segment[0] == '$' || segment.Contains('\0')) || path is "_id" or "_librarianVersion")
            throw new ArgumentException("Invalid document field path.", nameof(path));
        return path;
    }
    public async ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        await Store.Indexes.CreateOneAsync(new CreateIndexModel<BsonDocument>(new BsonDocument { { Path(fieldPath), 1 }, { "_id", 1 } },
            new CreateIndexOptions { Collation = Collation.Simple }), cancellationToken: cancellationToken).NoSync();
    }
    private static BsonValue Scalar(object? value)
    {
        BsonValue scalar = MongoJsonValue.FromJson(LibrarianJson.Element(value));
        if (scalar.IsBsonDocument || scalar.IsBsonArray) throw new ArgumentException("Index bounds must be JSON scalars.", nameof(value));
        return scalar;
    }
    private FilterDefinition<BsonDocument> Equality(string path, object? value) => Scope & Builders<BsonDocument>.Filter.Exists(Path(path)) & Builders<BsonDocument>.Filter.Eq(path, Scalar(value));
    private FilterDefinition<BsonDocument> Range(string path, object? minimum, object? maximum)
    {
        FilterDefinition<BsonDocument> filter = Scope & Builders<BsonDocument>.Filter.Exists(Path(path));
        BsonValue? lower = minimum is null ? null : Scalar(minimum), upper = maximum is null ? null : Scalar(maximum);
        if (lower is not null && upper is not null && ((!lower.IsNumeric || !upper.IsNumeric) && lower.BsonType != upper.BsonType || lower.CompareTo(upper) > 0))
            throw new ArgumentException("Range bounds must share a scalar type and be in ascending order.");
        if (lower is not null) filter &= Builders<BsonDocument>.Filter.Gte(path, lower);
        if (upper is not null) filter &= Builders<BsonDocument>.Filter.Lte(path, upper);
        return filter;
    }
    private async ValueTask<LibrarianQueryResult<T>> Page<T>(string path, FilterDefinition<BsonDocument> filter, bool range, bool descending, int skip, int take, CancellationToken token)
    {
        Check(token);
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take <= 0) throw new ArgumentOutOfRangeException(nameof(take));
        int direction = descending ? -1 : 1;
        BsonDocument order = range ? new BsonDocument { { path, direction }, { "_id", direction } } : new BsonDocument("_id", direction);
        List<BsonDocument> rows = await Store.Find(filter, new FindOptions { Collation = Collation.Simple }).Sort(order).Skip(skip).Limit(take).ToListAsync(token).NoSync();
        List<T> items = rows.Select(row => LibrarianJson.Deserialize<T>(Json(row))!).ToList();
        return new LibrarianQueryResult<T> { Items = items, Index = path, IndexEntriesExamined = rows.Count, DocumentsDeserialized = rows.Count };
    }
    public ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100, CancellationToken cancellationToken = default) => Page<T>(fieldPath, Equality(fieldPath, value), false, false, skip, take, cancellationToken);
    public ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null, bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default) => Page<T>(fieldPath, Range(fieldPath, minimum, maximum), true, descending, skip, take, cancellationToken);
    public async ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    { Check(cancellationToken); return checked((int)await Store.CountDocumentsAsync(Equality(fieldPath, value), cancellationToken: cancellationToken).NoSync()); }
    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    { Check(cancellationToken); return await Store.CountDocumentsAsync(Equality(fieldPath, value), new CountOptions { Limit = 1 }, cancellationToken).NoSync() != 0; }
    public async ValueTask<int> CountRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null, CancellationToken cancellationToken = default)
    { Check(cancellationToken); return checked((int)await Store.CountDocumentsAsync(Range(fieldPath, minimum, maximum), cancellationToken: cancellationToken).NoSync()); }
}
