using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.SurrealDb;

internal sealed partial class SurrealDbLibrarianContainer
{
    private readonly ConcurrentDictionary<string, byte> _indexes = new(StringComparer.Ordinal);
    private static string Field(string path) { SurrealDbIndexValue.ValidatePath(path); return "keys.k" + SurrealDbIndexValue.Hex(path); }
    private static string Index(string path) => "idx_" + SurrealDbIndexValue.Hex(path);
    public async ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        string field = Field(fieldPath);
        if (_indexes.ContainsKey(fieldPath)) return;
        await database.Query($"DEFINE INDEX IF NOT EXISTS {Index(fieldPath)} ON TABLE {table} FIELDS {field},sort;", null, cancellationToken).ConfigureAwait(false);
        _indexes.TryAdd(fieldPath, 0);
    }
    public ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        string encoded = SurrealDbIndexValue.Encode(value);
        return Indexed<T>(fieldPath, encoded, encoded, false, skip, take, cancellationToken);
    }
    public ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        string? min = minimum is null ? null : SurrealDbIndexValue.Encode(minimum);
        string? max = maximum is null ? null : SurrealDbIndexValue.Encode(maximum);
        if (min is not null && max is not null && (min[0] != max[0] || string.CompareOrdinal(min, max) > 0)) throw new ArgumentException("Range bounds must share a scalar type and be ordered.");
        return Indexed<T>(fieldPath, min, max, descending, skip, take, cancellationToken);
    }
    private async ValueTask<LibrarianQueryResult<T>> Indexed<T>(string path, string? min, string? max, bool descending, int skip, int take, CancellationToken token)
    {
        Check(token);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        await EnsureIndex(path, token).ConfigureAwait(false);
        string field = Field(path), direction = descending ? "DESC" : "ASC";
        var values = new Dictionary<string, object?> { ["min"] = min ?? "0", ["take"] = take, ["skip"] = skip };
        string predicate = field + " >= $min";
        if (max is not null) { values["max"] = max; predicate += " AND " + field + " <= $max"; }
        string sql = $"SELECT VALUE document FROM (SELECT document,keys,sort FROM {table} WITH INDEX {Index(path)} WHERE {predicate} ORDER BY {field} {direction},sort {direction} LIMIT $take START $skip);";
        var response = await database.Query(sql, values, token).ConfigureAwait(false);
        List<T> items = response.GetValues<string>(0).Select(json => LibrarianJson.Deserialize<T>(json)!).ToList();
        return new LibrarianQueryResult<T> { Items = items, Index = path, IndexEntriesExamined = items.Count, DocumentsDeserialized = items.Count };
    }
    public async ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        await EnsureIndex(fieldPath, cancellationToken).ConfigureAwait(false);
        var response = await database.Query($"SELECT VALUE count() FROM {table} WITH INDEX {Index(fieldPath)} WHERE {Field(fieldPath)} = $value GROUP ALL;",
            new Dictionary<string, object?> { ["value"] = SurrealDbIndexValue.Encode(value) }, cancellationToken).ConfigureAwait(false);
        return checked((int)response.GetValues<long>(0).SingleOrDefault());
    }
    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        await EnsureIndex(fieldPath, cancellationToken).ConfigureAwait(false);
        var response = await database.Query($"SELECT VALUE true FROM {table} WITH INDEX {Index(fieldPath)} WHERE {Field(fieldPath)} = $value LIMIT 1;",
            new Dictionary<string, object?> { ["value"] = SurrealDbIndexValue.Encode(value) }, cancellationToken).ConfigureAwait(false);
        return response.GetValues<bool>(0).Any();
    }
}
