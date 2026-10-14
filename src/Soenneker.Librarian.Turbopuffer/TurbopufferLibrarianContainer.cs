using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Turbopuffer.Exceptions;
using Turbopuffer.Models.Namespaces;
using Turbopuffer.Services;

namespace Soenneker.Librarian.Turbopuffer;

internal sealed partial class TurbopufferLibrarianContainer(INamespaceService service) : ILibrarianContainer
{
    private const string DocumentAttribute = "librarian_document";
    private const string RevisionAttribute = "librarian_revision";
    private const int PageSize = 1000;
    private volatile bool _disposed;

    private void Check(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
    }

    private static void ValidateId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (new UTF8Encoding(false, true).GetByteCount(id) > 64)
            throw new ArgumentException("turbopuffer document IDs must be at most 64 UTF-8 bytes.", nameof(id));
    }

    private static string Text(Row row, string attribute) =>
        row[attribute]?.GetString() ?? throw new InvalidDataException($"turbopuffer returned a document without '{attribute}'.");

    private static Row Encode(string id, string document, string revision)
    {
        ArgumentNullException.ThrowIfNull(document);
        using JsonDocument parsed = JsonDocument.Parse(document);
        return new Row().Set("id", id).Set(DocumentAttribute, document).Set(RevisionAttribute, revision);
    }

    private async ValueTask<IReadOnlyList<Row>> Query(Filter? filter, int take, bool includeDocument, CancellationToken token)
    {
        Check(token);
        try
        {
            NamespaceQueryResponse response = await service.Query(new NamespaceQueryParams
            {
                Filters = filter,
                RankBy = RankBy.Attribute("id", RankByAttributeOrder.ASC),
                TopK = take,
                IncludeAttributes = new List<string>(includeDocument ? [DocumentAttribute, RevisionAttribute] : []),
                Consistency = new NamespaceQueryParamsConsistency { Level = NamespaceQueryParamsConsistencyLevel.Strong }
            }, token).ConfigureAwait(false);
            return response.Rows ?? throw new InvalidDataException("turbopuffer returned no rows collection.");
        }
        catch (TurbopufferNotFoundException) { return []; }
    }

    private async ValueTask<bool> Write(NamespaceWriteParams parameters, CancellationToken token)
    {
        Check(token);
        try
        {
            NamespaceWriteResponse response = await service.Write(parameters, token).ConfigureAwait(false);
            if (response.RowsAffected is < 0 or > 1) throw new InvalidDataException("Unexpected turbopuffer affected row count.");
            return response.RowsAffected == 1;
        }
        catch (TurbopufferNotFoundException) when (parameters.UpsertRows is null) { return false; }
    }

    public async ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ValidateId(id);
        if (!await Write(new NamespaceWriteParams
        {
            UpsertRows = [Encode(id, document, Guid.NewGuid().ToString("N"))],
            UpsertCondition = Filter.Eq("id", null!),
            Schema = new Dictionary<string, AttributeSchemaConfig>
            {
                ["id"] = new() { Type = "string" },
                [DocumentAttribute] = new() { Type = "string", Filterable = false },
                [RevisionAttribute] = new() { Type = "string" }
            }
        }, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException($"Document '{id}' already exists.");
        return document;
    }

    public async ValueTask<LibrarianItem<string>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ValidateId(id);
        IReadOnlyList<Row> rows = await Query(Filter.Eq("id", id), 1, true, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0) return null;
        if (Text(rows[0], "id") != id) throw new InvalidDataException("turbopuffer returned an unexpected document ID.");
        return new LibrarianItem<string>(Text(rows[0], DocumentAttribute), Text(rows[0], RevisionAttribute));
    }

    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default) =>
        (await GetItemWithVersion(id, cancellationToken).ConfigureAwait(false))?.Document;

    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    private async ValueTask<LibrarianItem<string>?> Update(string id, string document, string? version, CancellationToken token)
    {
        Check(token);
        ValidateId(id);
        string revision = Guid.NewGuid().ToString("N");
        bool written = await Write(new NamespaceWriteParams
        {
            PatchRows = [Encode(id, document, revision)],
            PatchCondition = version is null ? null : Filter.Eq(RevisionAttribute, version)
        }, token).ConfigureAwait(false);
        return written ? new LibrarianItem<string>(document, revision) : null;
    }

    public async ValueTask<string?> UpdateItem(string id, string document, CancellationToken cancellationToken = default) =>
        (await Update(id, document, null, cancellationToken).ConfigureAwait(false))?.Document;

    public async ValueTask<string> UpdateItemStrict(string id, string document, CancellationToken cancellationToken = default) =>
        await UpdateItem(id, document, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public ValueTask<LibrarianItem<string>?> UpdateItemIfVersion(string id, string document, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return Update(id, document, version, cancellationToken);
    }

    private ValueTask<bool> Delete(string id, string? version, CancellationToken token)
    {
        Check(token);
        ValidateId(id);
        return Write(new NamespaceWriteParams
        {
            Deletes = [new ID(id)],
            DeleteCondition = version is null ? null : Filter.Eq(RevisionAttribute, version)
        }, token);
    }

    public async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default) =>
        _ = await Delete(id, null, cancellationToken).ConfigureAwait(false);

    public ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        return Delete(id, version, cancellationToken);
    }

    public IQueryable<T> BuildQueryable<T>() => throw Unsupported();
    public ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default) => throw Unsupported();
    public ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100, CancellationToken cancellationToken = default) => throw Unsupported();
    public ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) => throw Unsupported();
    public ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) => throw Unsupported();
    public ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null, bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default) => throw Unsupported();
    private static NotSupportedException Unsupported() => new("The turbopuffer document provider does not translate Librarian LINQ or secondary indexes.");
    public void Dispose() => _disposed = true;
}
