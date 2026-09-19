using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;

namespace Soenneker.Librarian.Postgres;

public sealed partial class PostgresLibrarianContainer
{
    public async ValueTask<int> CountRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null,
        CancellationToken cancellationToken = default)
    {
        Check();
        string? min = minimum is null ? null : PostgresIndexValue.Encode(minimum);
        string? max = maximum is null ? null : PostgresIndexValue.Encode(maximum);
        if (min is not null && max is not null && (min[0] != max[0] || string.CompareOrdinal(min, max) > 0))
            throw new ArgumentException("Range bounds must have the same scalar type and minimum must not exceed maximum.");
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).NoSync();
        await RequireIndex(connection, fieldPath, cancellationToken).NoSync();
        var values = new List<object> { fieldPath };
        string sql = "SELECT COUNT(*) FROM public.librarian_postgres_values WHERE database_key=$1 AND container=$2 AND path=$3";
        if (min is not null) { values.Add(min); sql += " AND value >= $" + (values.Count + 2); }
        if (max is not null) { values.Add(max); sql += " AND value <= $" + (values.Count + 2); }
        await using NpgsqlCommand command = Command(connection, sql, values.ToArray());
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken).NoSync())!);
    }

    public async ValueTask<string?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        Check();
        cancellationToken.ThrowIfCancellationRequested();
        if (ids.Count == 0) return [];
        var keys = new string[ids.Count];
        for (int i = 0; i < ids.Count; i++) keys[i] = Id(ids[i]);
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).NoSync();
        await using NpgsqlCommand command = Command(connection, """
            SELECT d.document FROM unnest($3::text[]) WITH ORDINALITY AS requested(id_key, position)
            LEFT JOIN public.librarian_postgres_documents d
              ON d.database_key=$1 AND d.container=$2 AND d.id_key=requested.id_key
            ORDER BY requested.position
            """, (object)keys);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).NoSync();
        var result = new string?[keys.Length];
        int index = 0;
        while (await reader.ReadAsync(cancellationToken).NoSync())
            result[index++] = reader.IsDBNull(0) ? null : reader.GetString(0);
        return result;
    }

    public async ValueTask<int> CountItems(CancellationToken cancellationToken = default)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).NoSync();
        await using NpgsqlCommand command = Command(connection,
            "SELECT COUNT(*) FROM public.librarian_postgres_documents WHERE database_key=$1 AND container=$2");
        return checked((int)(long)(await command.ExecuteScalarAsync(cancellationToken).NoSync())!);
    }
}
