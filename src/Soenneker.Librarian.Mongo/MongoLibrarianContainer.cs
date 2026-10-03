using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Mongo;

internal sealed class MongoLibrarianContainer(string name, MongoLibrarianDatabase database) : ILibrarianContainer
{
    private volatile bool _disposed;
    private void Check(CancellationToken token = default) { ObjectDisposedException.ThrowIf(_disposed, this); database.Check(token); }
    public void Dispose() => _disposed = true;

    public ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    { Check(cancellationToken); return database.EnsureIndex(name, fieldPath, cancellationToken); }

    private async ValueTask RequireIndex(string path, CancellationToken token)
    {
        Check(token);
        MongoIndexValue.ValidatePath(path);
        if (!(await database.ReadMetadata(token).ConfigureAwait(false)).Paths(name).Contains(path))
            throw new InvalidOperationException($"Index '{path}' does not exist.");
    }

    private static MongoQueryFilter Equality(string path, object? value)
    {
        string encoded = MongoIndexValue.Encode(value);
        return new MongoQueryFilter("term", path, "[" + encoded + "!", "[" + encoded + "!~");
    }

    private static MongoQueryFilter Range(string path, object? minimum, object? maximum)
    {
        string? lower = minimum is null ? null : MongoIndexValue.Encode(minimum);
        string? upper = maximum is null ? null : MongoIndexValue.Encode(maximum);
        if (lower is not null && upper is not null && (lower[0] != upper[0] || string.CompareOrdinal(lower, upper) > 0))
            throw new ArgumentException("Range bounds must share a scalar type and be in ascending order.");
        return new MongoQueryFilter("term", path, lower is null ? "-" : "[" + lower + "!", upper is null ? "+" : "[" + upper + "!~");
    }

    private async ValueTask<LibrarianQueryResult<T>> Page<T>(string path, MongoQueryFilter filter, string? order,
        bool descending, int skip, int take, CancellationToken token)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take <= 0) throw new ArgumentOutOfRangeException(nameof(take));
        await RequireIndex(path, token).ConfigureAwait(false);
        List<MongoDocument> rows = await database.Consistent(t => database.Select(new MongoSelection(name, filter, order, descending, skip, take), t), token).ConfigureAwait(false);
        var items = new List<T>(rows.Count);
        foreach (MongoDocument row in rows) { token.ThrowIfCancellationRequested(); items.Add(LibrarianJson.Deserialize<T>(row.Json)!); }
        return new LibrarianQueryResult<T> { Items = items, Index = path, IndexEntriesExamined = rows.Count, DocumentsDeserialized = rows.Count };
    }

    public ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        Page<T>(fieldPath, Equality(fieldPath, value), null, false, skip, take, cancellationToken);

    public ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        Page<T>(fieldPath, Range(fieldPath, minimum, maximum), fieldPath, descending, skip, take, cancellationToken);

    public async ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        await RequireIndex(fieldPath, cancellationToken).ConfigureAwait(false);
        return checked((int)await database.Count(new MongoSelection(name, Equality(fieldPath, value)), cancellationToken).ConfigureAwait(false));
    }
    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        await RequireIndex(fieldPath, cancellationToken).ConfigureAwait(false);
        return await database.Count(new MongoSelection(name, Equality(fieldPath, value), Take: 1), cancellationToken).ConfigureAwait(false) != 0;
    }
    public async ValueTask<int> CountRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null, CancellationToken cancellationToken = default)
    {
        await RequireIndex(fieldPath, cancellationToken).ConfigureAwait(false);
        return checked((int)await database.Count(new MongoSelection(name, Range(fieldPath, minimum, maximum)), cancellationToken).ConfigureAwait(false));
    }

    public IQueryable<T> BuildQueryable<T>()
    {
        Check();
        return new MongoQueryable<T>(database.BuildQueryable<T>(name), token =>
        {
            Check(token);
            return database.Initialize(token);
        });
    }

    public async ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        if (!await database.Mutate(name, id, document, "add", cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException($"Document '{id}' already exists.");
        return document;
    }
    public async ValueTask<string?> UpdateItem(string id, string document, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        return await database.Mutate(name, id, document, "update", cancellationToken).ConfigureAwait(false) ? document : null;
    }
    public async ValueTask<string> UpdateItemStrict(string id, string document, CancellationToken cancellationToken = default) =>
        await UpdateItem(id, document, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");
    public async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default)
    { Check(cancellationToken); await database.Mutate(name, id, null, "delete", cancellationToken).ConfigureAwait(false); }
    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(id);
        return (await database.ReadDocument(MongoDocument.Address(name, id), cancellationToken).ConfigureAwait(false))?.Json;
    }
    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");
    public ValueTask<string?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(ids);
        foreach (string id in ids) ArgumentNullException.ThrowIfNull(id);
        return database.Consistent(async token =>
        {
            var result = new string?[ids.Count];
            for (int i = 0; i < ids.Count; i++) result[i] = await GetItem(ids[i], token).ConfigureAwait(false);
            return result;
        }, cancellationToken);
    }
    public async ValueTask<int> CountItems(CancellationToken cancellationToken = default)
    { Check(cancellationToken); return checked((int)await database.Count(new MongoSelection(name, new MongoQueryFilter("all")), cancellationToken).ConfigureAwait(false)); }
    private ValueTask<List<MongoDocument>> All(CancellationToken token)
    { Check(token); return database.Consistent(t => database.Select(new MongoSelection(name, new MongoQueryFilter("all")), t), token); }
    public async ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default) =>
        (await All(cancellationToken).ConfigureAwait(false)).Select(x => x.Json).ToList();
    public async ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default) =>
        (await All(cancellationToken).ConfigureAwait(false)).Select(x => x.OriginalId).ToList();
    public async ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default) =>
        (await All(cancellationToken).ConfigureAwait(false)).Select(x => new IdValuePair { Id = x.OriginalId, Value = x.Json }).ToList();
    public ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    { Check(cancellationToken); return database.DeleteAll(name, cancellationToken); }
}
