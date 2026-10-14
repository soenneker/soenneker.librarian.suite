using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Dtos.IdValuePair;
using Turbopuffer.Exceptions;
using Turbopuffer.Models.Namespaces;

namespace Soenneker.Librarian.Turbopuffer;

internal sealed partial class TurbopufferLibrarianContainer
{
    public async ValueTask<string?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        ArgumentNullException.ThrowIfNull(ids);
        foreach (string id in ids) ValidateId(id);
        // A single query preserves a consistent view, including duplicate/missing input IDs.
        string[] unique = ids.Distinct(StringComparer.Ordinal).ToArray();
        if (unique.Length > 10000) throw new ArgumentException("A turbopuffer bulk read supports at most 10,000 distinct IDs.", nameof(ids));
        var result = new string?[ids.Count];
        if (unique.Length == 0) return result;
        IReadOnlyList<Row> rows = await Query(Filter.In("id", unique), unique.Length, true, cancellationToken).ConfigureAwait(false);
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        var requested = new HashSet<string>(unique, StringComparer.Ordinal);
        foreach (Row row in rows)
        {
            string id = Text(row, "id");
            if (!requested.Contains(id)) throw new InvalidDataException("turbopuffer returned an unrequested document ID.");
            documents.Add(id, Text(row, DocumentAttribute));
        }
        for (int i = 0; i < ids.Count; i++) result[i] = documents.GetValueOrDefault(ids[i]);
        return result;
    }

    public async ValueTask<int> CountItems(CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        try
        {
            NamespaceQueryResponse response = await service.Query(new NamespaceQueryParams
            {
                AggregateBy = new Dictionary<string, AggregateBy> { ["count"] = AggregateBy.Count() },
                Consistency = new NamespaceQueryParamsConsistency { Level = NamespaceQueryParamsConsistencyLevel.Strong }
            }, cancellationToken).ConfigureAwait(false);
            if (response.Aggregations is null || !response.Aggregations.TryGetValue("count", out JsonElement count))
                throw new InvalidDataException("turbopuffer returned no count aggregation.");
            return count.GetInt32();
        }
        catch (TurbopufferNotFoundException) { return 0; }
    }

    private async ValueTask<List<Row>> ReadAll(bool includeDocument, CancellationToken token)
    {
        Check(token);
        var result = new List<Row>();
        string? cursor = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            IReadOnlyList<Row> page = await Query(cursor is null ? null : Filter.Gt("id", cursor), PageSize, includeDocument, token).ConfigureAwait(false);
            foreach (Row row in page)
            {
                string id = Text(row, "id");
                if (!seen.Add(id)) throw new InvalidDataException("turbopuffer returned a repeated document while paging.");
                result.Add(row);
            }
            if (page.Count < PageSize) return result;
            cursor = Text(page[^1], "id");
        }
    }

    public async ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default) =>
        (await ReadAll(false, cancellationToken).ConfigureAwait(false)).Select(row => Text(row, "id")).ToList();

    public async ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default) =>
        (await ReadAll(true, cancellationToken).ConfigureAwait(false)).Select(row => Text(row, DocumentAttribute)).ToList();

    public async ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default) =>
        (await ReadAll(true, cancellationToken).ConfigureAwait(false)).Select(row => new IdValuePair
        {
            Id = Text(row, "id"), Value = Text(row, DocumentAttribute)
        }).ToList();

    public async ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        try
        {
            NamespaceWriteResponse response;
            do
            {
                Check(cancellationToken);
                response = await service.Write(new NamespaceWriteParams
                {
                    DeleteByFilter = Filter.NotEq("id", null!),
                    DeleteByFilterAllowPartial = true
                }, cancellationToken).ConfigureAwait(false);
                if (response.RowsRemaining == true && response.RowsAffected == 0)
                    throw new InvalidDataException("turbopuffer made no progress clearing the container.");
            } while (response.RowsRemaining == true);
        }
        catch (TurbopufferNotFoundException) { }
    }
}
