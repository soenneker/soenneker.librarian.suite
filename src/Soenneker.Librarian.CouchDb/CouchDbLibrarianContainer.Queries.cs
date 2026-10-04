using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Asyncs.Semaphores;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.CouchDb;

internal sealed partial class CouchDbLibrarianContainer
{
    private readonly AsyncSemaphore _indexGate = new(1);
    private readonly HashSet<string> _indexes = new(StringComparer.Ordinal);

    private static void ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith('_') || path.Any(char.IsControl) || path.Split('.').Any(segment =>
                segment.Length == 0 || segment.StartsWith('$') || segment.Contains('\\')))
            throw new ArgumentException(
                "Use a dot-separated JSON field path without reserved metadata, operators, or escapes.", nameof(path));
    }

    private string[] IndexFields(string path) =>
        (partition is null ? new[] { path, "_id" } : new[] { "partitionKey", path, "_id" })
        .Distinct(StringComparer.Ordinal).ToArray();

    private string IndexName(string path) => "librarian-" +
                                             Convert.ToHexStringLower(SHA256.HashData(
                                                 Encoding.UTF8.GetBytes(string.Join('\n', IndexFields(path)))));

    public async ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ValidatePath(fieldPath);

        using (await _indexGate.Acquire(cancellationToken).NoSync())
        {
            Check(cancellationToken);
            if (_indexes.Contains(fieldPath))
                return;
            var fields = new JsonArray();
            foreach (string field in IndexFields(fieldPath))
                fields.Add((JsonNode?)JsonValue.Create(field));
            string name = IndexName(fieldPath);
            var body = new JsonObject
            {
                ["index"] = new JsonObject { ["fields"] = fields }, ["name"] = name, ["ddoc"] = name, ["type"] = "json"
            };
            using HttpResponseMessage response =
                await database.Send(HttpMethod.Post, physical + "/_index", body, cancellationToken).NoSync();
            using JsonDocument result = await CouchDbLibrarianDatabase.Read(response, cancellationToken).NoSync();
            if (result.RootElement.GetProperty("result").GetString() is not ("created" or "exists"))
                throw new HttpRequestException("CouchDB did not confirm index creation.");
            _indexes.Add(fieldPath);
        }
    }

    private static JsonNode? Scalar(object? value) => value switch
    {
        null => null,
        string v => JsonValue.Create(v), bool v => JsonValue.Create(v),
        byte v => JsonValue.Create(v), sbyte v => JsonValue.Create(v), short v => JsonValue.Create(v),
        ushort v => JsonValue.Create(v),
        int v => JsonValue.Create(v), uint v => JsonValue.Create(v), long v => JsonValue.Create(v),
        ulong v => JsonValue.Create(v),
        decimal v => JsonValue.Create(v), double v when double.IsFinite(v) => JsonValue.Create(v),
        float v when float.IsFinite(v) => JsonValue.Create(v),
        JsonElement v when v.ValueKind is JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number
            or JsonValueKind.True or JsonValueKind.False => JsonNode.Parse(v.GetRawText()),
        _ => throw new ArgumentException("Index bounds must be finite JSON scalars.", nameof(value))
    };

    private JsonObject Query(string path, object? minimum, object? maximum, bool equality, bool descending, int skip,
        int take, bool idsOnly)
    {
        ValidatePath(path);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(take);
        JsonNode? lower = Scalar(minimum);
        JsonNode? upper = Scalar(maximum);
        if (!equality && lower is not null && upper is not null)
        {
            using JsonDocument left = JsonDocument.Parse(lower.ToJsonString());
            using JsonDocument right = JsonDocument.Parse(upper.ToJsonString());
            JsonValueKind a = left.RootElement.ValueKind;
            JsonValueKind b = right.RootElement.ValueKind;
            bool booleans = a is JsonValueKind.True or JsonValueKind.False && b is JsonValueKind.True or JsonValueKind.False;
            if (a != b && !booleans) throw new ArgumentException("Range bounds must have the same JSON scalar type.");
            if (a == JsonValueKind.Number && left.RootElement.GetDecimal() > right.RootElement.GetDecimal() ||
                booleans && left.RootElement.GetBoolean() && !right.RootElement.GetBoolean())
                throw new ArgumentException("The minimum must not exceed the maximum.");
            // String bounds retain CouchDB's ICU collation rather than CLR ordinal ordering.
        }
        var comparison = new JsonObject { ["$exists"] = true };
        if (equality)
            comparison["$eq"] = lower;
        else
        {
            comparison["$gte"] = lower;
            if (maximum is not null)
                comparison["$lte"] = upper;
        }

        var conditions = new JsonArray(new JsonObject { ["_id"] = new JsonObject { ["$gte"] = "d-", ["$lt"] = "d." } },
            new JsonObject { [path] = comparison });
        if (partition is not null)
            conditions.Add((JsonNode)new JsonObject { ["partitionKey"] = new JsonObject { ["$eq"] = partition } });
        var sort = new JsonArray();
        foreach (string field in IndexFields(path))
            sort.Add((JsonNode)new JsonObject { [field] = descending ? "desc" : "asc" });
        string name = IndexName(path);
        var body = new JsonObject
        {
            ["selector"] = new JsonObject { ["$and"] = conditions }, ["sort"] = sort,
            ["skip"] = skip, ["limit"] = take, ["use_index"] = new JsonArray(name, name),
            ["allow_fallback"] = false, ["execution_stats"] = true
        };
        if (idsOnly)
            body["fields"] = new JsonArray("_id");
        return body;
    }

    private async ValueTask<JsonDocument> Find(JsonObject query, CancellationToken token)
    {
        Check(token);
        using HttpResponseMessage response =
            await database.Send(HttpMethod.Post, physical + "/_find", query, token).NoSync();
        JsonDocument result = await CouchDbLibrarianDatabase.Read(response, token).NoSync();
        if (result.RootElement.TryGetProperty("warning", out _))
        {
            result.Dispose();
            throw new InvalidOperationException(
                "CouchDB could not honor the requested index. Call EnsureIndex before querying.");
        }

        return result;
    }

    private async ValueTask<LibrarianQueryResult<T>> Page<T>(string path, object? minimum, object? maximum,
        bool equality, bool descending, int skip, int take, CancellationToken token)
    {
        Check(token);
        _ = LibrarianJson.Contract(typeof(T));
        using JsonDocument result =
            await Find(Query(path, minimum, maximum, equality, descending, skip, take, false), token).NoSync();
        JsonElement documents = result.RootElement.GetProperty("docs");
        var items = new List<T>(documents.GetArrayLength());
        foreach (JsonElement document in documents.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            items.Add(LibrarianJson.Deserialize<T>(Decode(document))!);
        }

        int examined =
            result.RootElement.TryGetProperty("execution_stats", out JsonElement stats) &&
            stats.TryGetProperty("total_keys_examined", out JsonElement keys)
                ? keys.GetInt32()
                : items.Count;
        return new LibrarianQueryResult<T>
            { Items = items, Index = path, IndexEntriesExamined = examined, DocumentsDeserialized = items.Count };
    }

    public ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0,
        int take = 100, CancellationToken cancellationToken = default) =>
        Page<T>(fieldPath, value, null, true, false, skip, take, cancellationToken);

    public ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null,
        object? maximum = null, bool descending = false, int skip = 0, int take = 100,
        CancellationToken cancellationToken = default) =>
        Page<T>(fieldPath, minimum, maximum, false, descending, skip, take, cancellationToken);

    private async ValueTask<int> Count(string path, object? minimum, object? maximum, bool equality,
        CancellationToken token)
    {
        Check(token);
        JsonObject query = Query(path, minimum, maximum, equality, false, 0, 256, true);
        int count = 0;
        string? previous = null;
        while (true)
        {
            using JsonDocument result = await Find(query, token).NoSync();
            int found = result.RootElement.GetProperty("docs").GetArrayLength();
            count = checked(count + found);
            if (found < 256)
                return count;
            string? next = result.RootElement.GetProperty("bookmark").GetString();
            if (string.IsNullOrEmpty(next) || next == previous)
                throw new HttpRequestException("CouchDB pagination did not advance.");
            query["bookmark"] = next;
            previous = next;
        }
    }

    public ValueTask<int>
        CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) =>
        Count(fieldPath, value, null, true, cancellationToken);

    public ValueTask<int> CountRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null,
        CancellationToken cancellationToken = default) => Count(fieldPath, minimum, maximum, false, cancellationToken);

    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument result =
            await Find(Query(fieldPath, value, null, true, false, 0, 1, true), cancellationToken).NoSync();
        return result.RootElement.GetProperty("docs").GetArrayLength() != 0;
    }
}
