using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Cosmos;

internal sealed partial class CosmosLibrarianContainer
{
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private readonly HashSet<string> _indexes = new(StringComparer.Ordinal);
    private static string[] Segments(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string[] segments = path.Split('.');
        if (segments.Any(string.IsNullOrEmpty)) throw new ArgumentException("Index paths cannot contain empty segments.", nameof(path));
        return segments;
    }
    private static string Field(string path) => "c.body" + string.Concat(Segments(path).Select(segment => "[" + JsonSerializer.Serialize(segment) + "]"));
    private static string IndexPath(string path) => "/body/" + string.Join('/', Segments(path).Select(segment => JsonSerializer.Serialize(segment)));

    public async ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        string path = IndexPath(fieldPath);
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_indexes.Contains(path)) return;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                ContainerResponse response = await store.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                ContainerProperties properties = response.Resource;
                bool exists = properties.IndexingPolicy.CompositeIndexes.Any(index => index.Count == 2 && index[0].Path == path &&
                    index[0].Order == CompositePathSortOrder.Ascending && index[1].Path == "/sortId" && index[1].Order == CompositePathSortOrder.Ascending);
                if (!exists)
                {
                    properties.IndexingPolicy.CompositeIndexes.Add(new Collection<CompositePath>
                    {
                        new() { Path = path, Order = CompositePathSortOrder.Ascending },
                        new() { Path = "/sortId", Order = CompositePathSortOrder.Ascending }
                    });
                    try { await store.ReplaceContainerAsync(properties, new ContainerRequestOptions { IfMatchEtag = properties.ETag }, cancellationToken).ConfigureAwait(false); }
                    catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed) { continue; }
                }
                // Cosmos builds new indexes online. Wait before issuing the ordered range query that requires them.
                for (int poll = 0; poll < 120; poll++)
                {
                    ContainerResponse progress = await store.ReadContainerAsync(new ContainerRequestOptions { PopulateQuotaInfo = true }, cancellationToken).ConfigureAwait(false);
                    string? percent = progress.Headers["x-ms-documentdb-collection-index-transformation-progress"];
                    if (percent is null or "100") { _indexes.Add(path); return; }
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                throw new TimeoutException("Cosmos composite index creation is still in progress; retry the query later.");
            }
            throw new TimeoutException("Cosmos index policy update exceeded five concurrency retries.");
        }
        finally { _indexGate.Release(); }
    }

    private static object? Scalar(object? value)
    {
        JsonElement element = LibrarianJson.Element(value);
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException("Index bounds must be JSON scalars.", nameof(value))
        };
    }
    private QueryDefinition Indexed(string path, object? minimum, object? maximum, bool equality, string select, string suffix = "")
    {
        string field = Field(path);
        var query = Query(select, "AND IS_DEFINED(" + field + ")" + (equality ? " AND " + field + " = @minimum" :
            (minimum is null ? "" : " AND " + field + " >= @minimum") + (maximum is null ? "" : " AND " + field + " <= @maximum")) + " " + suffix);
        if (equality || minimum is not null) query.WithParameter("@minimum", Scalar(minimum));
        if (!equality && maximum is not null) query.WithParameter("@maximum", Scalar(maximum));
        return query;
    }
    private async ValueTask<LibrarianQueryResult<T>> Page<T>(string path, object? minimum, object? maximum, bool equality,
        bool descending, int skip, int take, CancellationToken token)
    {
        if (skip < 0) throw new ArgumentOutOfRangeException(nameof(skip));
        if (take <= 0) throw new ArgumentOutOfRangeException(nameof(take));
        string direction = descending ? " DESC" : " ASC";
        if (!equality) await EnsureIndex(path, token).ConfigureAwait(false);
        string order = equality ? "c.sortId" : Field(path) + direction + ", c.sortId";
        QueryDefinition query = Indexed(path, minimum, maximum, equality, "c.rawJson", "ORDER BY " + order + direction + " OFFSET @skip LIMIT @take")
            .WithParameter("@skip", skip).WithParameter("@take", take);
        List<T> items = await Query(query, value => LibrarianJson.Deserialize<T>(value.GetString()!)!, token).ConfigureAwait(false);
        return new LibrarianQueryResult<T> { Items = items, Index = path, IndexEntriesExamined = items.Count, DocumentsDeserialized = items.Count };
    }
    public ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        Page<T>(fieldPath, value, null, true, false, skip, take, cancellationToken);
    public ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        Page<T>(fieldPath, minimum, maximum, false, descending, skip, take, cancellationToken);
    public async ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) =>
        (await Query(Indexed(fieldPath, value, null, true, "COUNT(1)"), value => value.GetInt32(), cancellationToken).ConfigureAwait(false)).Single();
    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) =>
        (await Query(Indexed(fieldPath, value, null, true, "1", "OFFSET 0 LIMIT 1"), value => value.GetInt32(), cancellationToken).ConfigureAwait(false)).Count != 0;
    public async ValueTask<int> CountRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null, CancellationToken cancellationToken = default) =>
        (await Query(Indexed(fieldPath, minimum, maximum, false, "COUNT(1)"), value => value.GetInt32(), cancellationToken).ConfigureAwait(false)).Single();
}
