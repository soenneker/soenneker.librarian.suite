using Soenneker.Extensions.ValueTask;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Cosmos;

internal sealed partial class CosmosLibrarianContainer
{
    private static string[] Segments(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string[] segments = path.Split('.');
        if (segments.Any(string.IsNullOrEmpty)) throw new ArgumentException("Index paths cannot contain empty segments.", nameof(path));
        return segments;
    }
    private static string Field(string path) => "c" + string.Concat(Segments(path).Select(segment => "[" + System.Text.Json.JsonSerializer.Serialize(segment, CosmosInternalJsonContext.Default.String) + "]"));
    public ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        Segments(fieldPath);
        return ValueTask.CompletedTask;
    }
    private static object? Scalar(object? value)
    {
        if (value is null or string or bool or decimal or byte or sbyte or short or ushort or int or uint or long or ulong) return value;
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
    private QueryDefinition Indexed(string path, object? minimum, object? maximum, bool equality, string select, string suffix = "", string? field = null)
    {
        field ??= Field(path);
        QueryDefinition query = Query(select, "AND IS_DEFINED(" + field + ")" + (equality ? " AND " + field + " = @minimum" :
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

        string field = Field(path);
        string order = (equality ? "c.id" : field) + direction;
        QueryDefinition query = Indexed(path, minimum, maximum, equality, "c", "ORDER BY " + order + " OFFSET @skip LIMIT @take", field)
            .WithParameter("@skip", skip).WithParameter("@take", take);
        var contract = (JsonTypeInfo<T>)LibrarianJson.Contract(typeof(T));
        List<T> items = await Query(query, value =>
        {
            using var buffer = new CosmosJsonBuffer();
            WriteDocument(buffer, value);
            return JsonSerializer.Deserialize(buffer.WrittenSpan, contract)!;
        }, token).NoSync();
        return new LibrarianQueryResult<T> { Items = items, Index = path, IndexEntriesExamined = items.Count, DocumentsDeserialized = items.Count };
    }
    public ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        Page<T>(fieldPath, value, null, true, false, skip, take, cancellationToken);
    public ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        Page<T>(fieldPath, minimum, maximum, false, descending, skip, take, cancellationToken);
    public ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) =>
        QueryScalar(Indexed(fieldPath, value, null, true, "COUNT(1)"), false, cancellationToken);
    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) =>
        await QueryScalar(Indexed(fieldPath, value, null, true, "1", "OFFSET 0 LIMIT 1"), true, cancellationToken).NoSync() != 0;
    public ValueTask<int> CountRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null, CancellationToken cancellationToken = default) =>
        QueryScalar(Indexed(fieldPath, minimum, maximum, false, "COUNT(1)"), false, cancellationToken);
}
