using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Postgres;

public sealed partial class PostgresLibrarianContainer
{
    private async ValueTask<bool> HasIndex(NpgsqlConnection connection, string path, CancellationToken token)
    {
        await using NpgsqlCommand command = Command(connection,
            "SELECT EXISTS(SELECT 1 FROM public.librarian_postgres_indexes WHERE database_key=$1 AND container=$2 AND path=$3)", path);
        return (bool)(await command.ExecuteScalarAsync(token).NoSync())!;
    }

    public async ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    {
        Check();
        PostgresIndexValue.ValidatePath(fieldPath);
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).NoSync();
        if (await HasIndex(connection, fieldPath, cancellationToken).NoSync()) return;
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).NoSync();
        await _database.LockWrites(connection, cancellationToken).NoSync();
        if (await HasIndex(connection, fieldPath, cancellationToken).NoSync()) return;
        await using (NpgsqlCommand command = Command(connection,
            "INSERT INTO public.librarian_postgres_indexes (database_key,container,path) VALUES ($1,$2,$3)", fieldPath))
            await command.ExecuteNonQueryAsync(cancellationToken).NoSync();

        // Bounded keyset pages keep index construction from buffering the whole container.
        string? after = null;
        while (true)
        {
            var entries = new List<(string Id, string? Value)>();
            string predicate = after is null ? "" : " AND id_key>$3";
            await using (NpgsqlCommand command = Command(connection,
                "SELECT id_key,document FROM public.librarian_postgres_documents WHERE database_key=$1 AND container=$2" + predicate + " ORDER BY id_key LIMIT 256",
                after is null ? [] : [after]))
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).NoSync())
            {
                while (await reader.ReadAsync(cancellationToken).NoSync())
                {
                    using JsonDocument json = JsonDocument.Parse(reader.GetString(1));
                    entries.Add((reader.GetString(0), PostgresIndexValue.Read(json.RootElement, fieldPath)));
                }
            }
            if (entries.Count == 0) break;
            foreach ((string id, string? value) in entries)
                if (value is not null) await InsertValue(connection, id, fieldPath, value, cancellationToken).NoSync();
            after = entries[^1].Id;
        }
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).NoSync();
    }

    private async ValueTask RequireIndex(NpgsqlConnection connection, string path, CancellationToken token)
    {
        PostgresIndexValue.ValidatePath(path);
        if (!await HasIndex(connection, path, token).NoSync()) throw new InvalidOperationException($"Index '{path}' does not exist.");
    }

    private async ValueTask<LibrarianQueryResult<T>> Indexed<T>(string path, string? minimum, string? maximum, bool descending,
        int skip, int take, CancellationToken token)
    {
        Check();
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        await using NpgsqlConnection connection = await _database.Open(token).NoSync();
        await RequireIndex(connection, path, token).NoSync();
        var values = new List<object> { path };
        var bounds = "";
        if (minimum is not null) { values.Add(minimum); bounds += $" AND v.value>=${values.Count + 2}"; }
        if (maximum is not null) { values.Add(maximum); bounds += $" AND v.value<=${values.Count + 2}"; }
        string direction = descending ? "DESC" : "ASC";
        values.Add(take);
        int limit = values.Count + 2;
        values.Add(skip);
        // Select the index page before joining documents; skipped documents never cross the network.
        var sql = $"""
                   SELECT d.document FROM (
                       SELECT v.id_key,v.value FROM public.librarian_postgres_values v
                       WHERE v.database_key=$1 AND v.container=$2 AND v.path=$3{bounds}
                       ORDER BY v.value {direction},v.id_key {direction} LIMIT ${limit} OFFSET ${limit + 1}
                   ) page JOIN public.librarian_postgres_documents d ON d.database_key=$1 AND d.container=$2 AND d.id_key=page.id_key
                   ORDER BY page.value {direction},page.id_key {direction}
                   """;
        await using NpgsqlCommand command = Command(connection, sql, values.ToArray());
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token).NoSync();
        var items = new List<T>();
        while (await reader.ReadAsync(token).NoSync()) items.Add(LibrarianJson.Deserialize<T>(reader.GetString(0))!);
        return new LibrarianQueryResult<T> { Items = items, Index = path, IndexEntriesExamined = items.Count, DocumentsDeserialized = items.Count };
    }

    public ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100,
        CancellationToken cancellationToken = default)
    {
        string encoded = PostgresIndexValue.Encode(value);
        return Indexed<T>(fieldPath, encoded, encoded, false, skip, take, cancellationToken);
    }

    public ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        string? min = minimum is null ? null : PostgresIndexValue.Encode(minimum);
        string? max = maximum is null ? null : PostgresIndexValue.Encode(maximum);
        if (min is not null && max is not null && min[0] != max[0]) throw new ArgumentException("Range bounds must have the same scalar type.");
        return Indexed<T>(fieldPath, min, max, descending, skip, take, cancellationToken);
    }

    private async ValueTask<object> Aggregate(string path, object? value, bool exists, CancellationToken token)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(token).NoSync();
        await RequireIndex(connection, path, token).NoSync();
        var from = "FROM public.librarian_postgres_values WHERE database_key=$1 AND container=$2 AND path=$3 AND value=$4";
        await using NpgsqlCommand command = Command(connection,
            exists ? "SELECT EXISTS(SELECT 1 " + from + ")" : "SELECT COUNT(*) " + from, path, PostgresIndexValue.Encode(value));
        return (await command.ExecuteScalarAsync(token).NoSync())!;
    }

    public async ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) =>
        checked((int)(long)await Aggregate(fieldPath, value, false, cancellationToken).NoSync());

    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) =>
        (bool)await Aggregate(fieldPath, value, true, cancellationToken).NoSync();
}
