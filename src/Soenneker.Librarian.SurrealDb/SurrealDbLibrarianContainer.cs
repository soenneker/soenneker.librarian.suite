using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using SurrealDb.Net.Models.Response;

namespace Soenneker.Librarian.SurrealDb;

internal sealed partial class SurrealDbLibrarianContainer(SurrealDbLibrarianDatabase database, string table) : ILibrarianContainer
{
    private volatile bool _disposed;
    private void Check(CancellationToken token = default) { ObjectDisposedException.ThrowIf(_disposed, this); database.Check(token); }
    public void Dispose() => _disposed = true;
    public IQueryable<T> BuildQueryable<T>() { Check(); return new LibrarianQueryable<T>(new SurrealDbQueryProvider<T>(this)); }

    internal string Record(string id, string parameter, Dictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(id);
        values[parameter] = SurrealDbIndexValue.Hex(id);
        return $"type::record('{table}', ${parameter})";
    }
    internal string Write(string record, string id, string? document, string prefix, Dictionary<string, object?> values)
    {
        if (document is null) return $"DELETE {record}; ";
        using JsonDocument parsed = JsonDocument.Parse(document);
        values[prefix + "document"] = document;
        values[prefix + "original"] = id;
        values[prefix + "sort"] = SurrealDbIndexValue.Hex(id);
        values[prefix + "revision"] = Guid.NewGuid().ToString("N");
        // A separate scalar map preserves exact JSON and ordinal/decimal index ordering without native type coercion.
        var keys = new Dictionary<string, string>();
        Flatten(parsed.RootElement, "", keys);
        values[prefix + "keys"] = keys;
        return $"UPSERT {record} CONTENT {{document: ${prefix}document, original: ${prefix}original, sort: ${prefix}sort, revision: ${prefix}revision, keys: ${prefix}keys}}; ";
    }
    private static void Flatten(JsonElement element, string path, Dictionary<string, string> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                // Dotted keys cannot be addressed by the public dotted-path index contract.
                if (property.Name.Contains('.') || property.Name.Length == 0) continue;
                Flatten(property.Value, path.Length == 0 ? property.Name : path + "." + property.Name, values);
            }
        }
        else if (path.Length != 0 && element.ValueKind != JsonValueKind.Array)
            values["k" + SurrealDbIndexValue.Hex(path)] = SurrealDbIndexValue.Encode(element);
    }
    public async ValueTask<LibrarianItem<string>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        var values = new Dictionary<string, object?>();
        string record = Record(id, "id", values);
        SurrealDbResponse response = await database.Query($"SELECT VALUE [document, revision] FROM {record};", values, cancellationToken).ConfigureAwait(false);
        string[]? item = response.GetValues<string[]>(0).SingleOrDefault();
        return item is null ? null : new LibrarianItem<string>(item[0], item[1]);
    }
    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default) => (await GetItemWithVersion(id, cancellationToken).ConfigureAwait(false))?.Document;
    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) => await GetItem(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");
    private async ValueTask<string?> Mutate(string id, string? document, string mode, string? version, CancellationToken token)
    {
        Check(token);
        var values = new Dictionary<string, object?>();
        string record = Record(id, "id", values);
        string sql = "";
        if (version is not null) { values["expected"] = version; sql = SurrealDbLibrarianDatabase.Condition($"(SELECT VALUE revision FROM {record}) = [$expected]"); }
        else if (mode == "add") sql = SurrealDbLibrarianDatabase.Condition($"(SELECT VALUE revision FROM {record}) = []");
        else if (mode == "update") sql = SurrealDbLibrarianDatabase.Condition($"(SELECT VALUE revision FROM {record}) != []");
        sql += Write(record, id, document, "w", values);
        bool committed = await database.Transaction(sql, values, token).ConfigureAwait(false);
        if (!committed && mode == "add") throw new InvalidOperationException($"Document '{id}' already exists.");
        return committed ? document is null ? "deleted" : (string)values["wrevision"]! : null;
    }
    public async ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(document); await Mutate(id, document, "add", null, cancellationToken).ConfigureAwait(false); return document; }
    public async ValueTask<string?> UpdateItem(string id, string document, CancellationToken cancellationToken = default)
    { ArgumentNullException.ThrowIfNull(document); return await Mutate(id, document, "update", null, cancellationToken).ConfigureAwait(false) is null ? null : document; }
    public async ValueTask<string> UpdateItemStrict(string id, string document, CancellationToken cancellationToken = default) => await UpdateItem(id, document, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");
    public async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default) => _ = await Mutate(id, null, "delete", null, cancellationToken).ConfigureAwait(false);
    public async ValueTask<LibrarianItem<string>?> UpdateItemIfVersion(string id, string document, string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document); ArgumentException.ThrowIfNullOrWhiteSpace(version);
        string? next = await Mutate(id, document, "update", version, cancellationToken).ConfigureAwait(false);
        return next is null ? null : new LibrarianItem<string>(document, next);
    }
    public async ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default)
    { ArgumentException.ThrowIfNullOrWhiteSpace(version); return await Mutate(id, null, "delete", version, cancellationToken).ConfigureAwait(false) is not null; }
    public async ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default)
    { Check(cancellationToken); return (await database.Query($"SELECT VALUE document FROM {table};", null, cancellationToken).ConfigureAwait(false)).GetValues<string>(0).ToList(); }
    public async ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default)
    { Check(cancellationToken); return (await database.Query($"SELECT VALUE original FROM {table};", null, cancellationToken).ConfigureAwait(false)).GetValues<string>(0).ToList(); }
    public async ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        return (await database.Query($"SELECT VALUE [original,document] FROM {table};", null, cancellationToken).ConfigureAwait(false))
            .GetValues<string[]>(0).Select(row => new IdValuePair { Id = row[0], Value = row[1] }).ToList();
    }
    public async ValueTask<int> CountItems(CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        return checked((int)(await database.Query($"SELECT VALUE count() FROM {table} GROUP ALL;", null, cancellationToken).ConfigureAwait(false)).GetValues<long>(0).SingleOrDefault());
    }
    public async ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    { Check(cancellationToken); await database.Transaction($"DELETE {table};", new Dictionary<string, object?>(), cancellationToken).ConfigureAwait(false); }
}
