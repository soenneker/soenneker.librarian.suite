using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Atomics.ValueBools;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.CouchDb;

internal sealed partial class CouchDbLibrarianContainer(CouchDbLibrarianDatabase database, string physical, string? partition) : ILibrarianContainer
{
    private ValueAtomicBool _disposed = new(false);
    private void Check(CancellationToken token = default) { ObjectDisposedException.ThrowIf(_disposed.Value, this); database.Check(token); }
    public void Dispose() => _disposed.TrySetTrue();

    private string Identity(string id)
    {
        var address = LibrarianDocumentJson.Address(id, partition);
        return "d-" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(address.Partition)) + "-" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(address.Id));
    }

    private JsonObject Encode(string id, string document, string? revision = null)
    {
        JsonElement parsed = LibrarianDocumentJson.Parse(id, document, partition);
        foreach (JsonProperty property in parsed.EnumerateObject())
            if (property.Name.StartsWith('_')) throw new ArgumentException("CouchDB reserves top-level fields starting with an underscore.", nameof(document));
        var result = JsonNode.Parse(parsed.GetRawText())!.AsObject();
        result["_id"] = Identity(id);
        if (revision is not null) result["_rev"] = revision;
        return result;
    }

    private static string Decode(JsonElement document)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.EnumerateObject())
                if (!property.Name.StartsWith('_')) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public IQueryable<T> BuildQueryable<T>()
    {
        Check();
        throw new NotSupportedException("CouchDB LINQ translation is not supported. Use EnsureIndex, FindByIndex, or FindRangeByIndex with JSON field paths.");
    }

    public async ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        JsonObject body = Encode(id, document);
        using HttpResponseMessage response = await database.Send(HttpMethod.Put, physical + "/" + Identity(id), body, cancellationToken).NoSync();
        if (response.StatusCode == HttpStatusCode.Conflict) throw new InvalidOperationException($"Document '{id}' already exists or has a conflicting revision.");
        using JsonDocument result = await CouchDbLibrarianDatabase.Read(response, cancellationToken).NoSync();
        ValidateWrite(result);
        return document;
    }

    private static string ValidateWrite(JsonDocument result)
    {
        if (!result.RootElement.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.True ||
            !result.RootElement.TryGetProperty("rev", out JsonElement rev) || string.IsNullOrEmpty(rev.GetString()))
            throw new HttpRequestException("CouchDB did not confirm the document write.");
        return rev.GetString()!;
    }

    public async ValueTask<LibrarianItem<string>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        using HttpResponseMessage response = await database.Send(HttpMethod.Get, physical + "/" + Identity(id), null, cancellationToken).NoSync();
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            using JsonDocument error = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).NoSync());
            if (error.RootElement.TryGetProperty("reason", out JsonElement reason) && reason.GetString() is "missing" or "deleted") return null;
            response.EnsureSuccessStatusCode();
        }
        using JsonDocument result = await CouchDbLibrarianDatabase.Read(response, cancellationToken).NoSync();
        return new LibrarianItem<string>(Decode(result.RootElement), result.RootElement.GetProperty("_rev").GetString()!);
    }

    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default) =>
        (await GetItemWithVersion(id, cancellationToken).NoSync())?.Document;
    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).NoSync() ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask<LibrarianItem<string>?> UpdateItemIfVersion(string id, string document, string version, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        JsonObject body = Encode(id, document, version);
        using HttpResponseMessage response = await database.Send(HttpMethod.Put, physical + "/" + Identity(id), body, cancellationToken).NoSync();
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        using JsonDocument result = await CouchDbLibrarianDatabase.Read(response, cancellationToken).NoSync();
        return new LibrarianItem<string>(document, ValidateWrite(result));
    }

    public async ValueTask<string?> UpdateItem(string id, string document, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        _ = Encode(id, document);
        LibrarianItem<string>? current = await GetItemWithVersion(id, cancellationToken).NoSync();
        if (current is null) return null;
        LibrarianItem<string>? updated = await UpdateItemIfVersion(id, document, current.Version, cancellationToken).NoSync();
        return updated?.Document ?? throw new LibrarianConcurrencyException($"Document '{id}' changed during the update.");
    }
    public async ValueTask<string> UpdateItemStrict(string id, string document, CancellationToken cancellationToken = default) =>
        await UpdateItem(id, document, cancellationToken).NoSync() ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        using HttpResponseMessage response = await database.Send(HttpMethod.Delete, physical + "/" + Identity(id) + "?rev=" + Uri.EscapeDataString(version), null, cancellationToken).NoSync();
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound) return false;
        using JsonDocument result = await CouchDbLibrarianDatabase.Read(response, cancellationToken).NoSync();
        ValidateWrite(result);
        return true;
    }
    public async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default)
    {
        LibrarianItem<string>? current = await GetItemWithVersion(id, cancellationToken).NoSync();
        if (current is not null && !await DeleteItemIfVersion(id, current.Version, cancellationToken).NoSync())
            throw new LibrarianConcurrencyException($"Document '{id}' changed during deletion.");
    }

    public async ValueTask<string?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(ids);
        var keys = new JsonArray();
        foreach (string id in ids) keys.Add((JsonNode?)JsonValue.Create(Identity(id)));
        if (ids.Count == 0) return [];
        using HttpResponseMessage response = await database.Send(HttpMethod.Post, physical + "/_all_docs?include_docs=true", new JsonObject { ["keys"] = keys }, cancellationToken).NoSync();
        using JsonDocument result = await CouchDbLibrarianDatabase.Read(response, cancellationToken).NoSync();
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonElement row in result.RootElement.GetProperty("rows").EnumerateArray())
            if (row.TryGetProperty("doc", out JsonElement doc) && doc.ValueKind == JsonValueKind.Object &&
                !(doc.TryGetProperty("_deleted", out JsonElement deleted) && deleted.ValueKind == JsonValueKind.True))
                found[row.GetProperty("id").GetString()!] = Decode(doc);
        return ids.Select(id => found.GetValueOrDefault(Identity(id))).ToArray();
    }

    private async IAsyncEnumerable<JsonElement> All([EnumeratorCancellation] CancellationToken token)
    {
        Check(token);
        string? after = null;
        do
        {
            string path = physical + "/_all_docs?include_docs=true&limit=256";
            if (after is not null) path += "&skip=1&startkey=" + Uri.EscapeDataString(JsonValue.Create(after)!.ToJsonString());
            using HttpResponseMessage response = await database.Send(HttpMethod.Get, path, null, token).NoSync();
            using JsonDocument result = await CouchDbLibrarianDatabase.Read(response, token).NoSync();
            JsonElement rows = result.RootElement.GetProperty("rows");
            if (rows.GetArrayLength() == 0) break;
            foreach (JsonElement row in rows.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                string id = row.GetProperty("id").GetString()!;
                if (id.StartsWith("d-", StringComparison.Ordinal) && row.TryGetProperty("doc", out JsonElement doc) && doc.ValueKind == JsonValueKind.Object &&
                    (partition is null || doc.GetProperty("partitionKey").GetString() == partition)) yield return doc;
            }
            string next = rows[rows.GetArrayLength() - 1].GetProperty("id").GetString()!;
            if (next == after) throw new HttpRequestException("CouchDB pagination did not advance.");
            after = next;
            if (rows.GetArrayLength() < 256) break;
        } while (true);
    }

    public async ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default)
    {
        var items = new List<string>();
        await foreach (JsonElement document in All(cancellationToken).ConfigureAwait(false)) items.Add(Decode(document));
        return items;
    }
    public async ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        await foreach (JsonElement document in All(cancellationToken).ConfigureAwait(false)) ids.Add(LibrarianDocumentJson.Id(document));
        return ids;
    }
    public async ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default)
    {
        var items = new List<IdValuePair>();
        await foreach (JsonElement document in All(cancellationToken).ConfigureAwait(false))
            items.Add(new IdValuePair { Id = LibrarianDocumentJson.Id(document), Value = Decode(document) });
        return items;
    }
    public async ValueTask<int> CountItems(CancellationToken cancellationToken = default)
    {
        int count = 0;
        await foreach (JsonElement document in All(cancellationToken).ConfigureAwait(false)) count = checked(count + 1);
        return count;
    }
    public async ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    {
        // Finish keyset paging before deleting its boundary documents.
        var revisions = new List<(string Id, string Version)>();
        await foreach (JsonElement document in All(cancellationToken).ConfigureAwait(false))
            revisions.Add((LibrarianDocumentJson.Id(document), document.GetProperty("_rev").GetString()!));
        foreach ((string id, string version) in revisions)
            if (!await DeleteItemIfVersion(id, version, cancellationToken).NoSync())
                throw new LibrarianConcurrencyException("A document changed during the clear operation; some documents may already have been deleted.");
    }
}
