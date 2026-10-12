using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Turso;
using System.Data.Common;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Turso;

internal sealed partial class TursoLibrarianContainer
{
    private async ValueTask<bool> HasIndex(DbConnection connection, string path, CancellationToken token)
    {
        await using DbCommand command = Command(connection,
            "SELECT EXISTS(SELECT 1 FROM librarian_turso_indexes WHERE database_key=$1 AND container=$2 AND path=$3)", path);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).NoSync()) != 0;
    }

    public async ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    {
        Check();
        TursoIndexValue.ValidatePath(fieldPath);
        await using DbConnection connection = await _database.Open(cancellationToken).NoSync();
        if (await HasIndex(connection, fieldPath, cancellationToken).NoSync()) return;
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).NoSync();
        await _database.LockWrites(connection, cancellationToken).NoSync();
        if (await HasIndex(connection, fieldPath, cancellationToken).NoSync()) return;
        await using (DbCommand command = Command(connection,
            "INSERT INTO librarian_turso_indexes (database_key,container,path) VALUES ($1,$2,$3)", fieldPath))
            await command.ExecuteNonQueryAsync(cancellationToken).NoSync();

        // Bounded keyset pages keep index construction from buffering the whole container.
        string? after = null;
        while (true)
        {
            var entries = new List<(string Id, string? Value)>();
            string predicate = after is null ? "" : " AND id_key>$3";
            await using (DbCommand command = Command(connection,
                "SELECT id_key,document FROM librarian_turso_documents WHERE database_key=$1 AND container=$2" + predicate + " ORDER BY id_key LIMIT 256",
                after is null ? [] : [after]))
            await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).NoSync())
            {
                while (await reader.ReadAsync(cancellationToken).NoSync())
                {
                    using JsonDocument json = JsonDocument.Parse(reader.GetString(1));
                    entries.Add((reader.GetString(0), TursoIndexValue.Read(json.RootElement, fieldPath)));
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

    private async ValueTask RequireIndex(DbConnection connection, string path, CancellationToken token)
    {
        TursoIndexValue.ValidatePath(path);
        if (!await HasIndex(connection, path, token).NoSync()) throw new InvalidOperationException($"Index '{path}' does not exist.");
    }

    private async ValueTask<LibrarianQueryResult<T>> Indexed<T>(string path, string? minimum, string? maximum, bool descending,
        int skip, int take, CancellationToken token)
    {
        Check();
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        await using DbConnection connection = await _database.Open(token).NoSync();
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
                       SELECT v.id_key,v.value FROM librarian_turso_values v
                       WHERE v.database_key=$1 AND v.container=$2 AND v.path=$3{bounds}
                       ORDER BY v.value {direction},v.id_key {direction} LIMIT ${limit} OFFSET ${limit + 1}
                   ) page JOIN librarian_turso_documents d ON d.database_key=$1 AND d.container=$2 AND d.id_key=page.id_key
                   ORDER BY page.value {direction},page.id_key {direction}
                   """;
        await using DbCommand command = Command(connection, sql, values.ToArray());
        await using DbDataReader reader = await command.ExecuteReaderAsync(token).NoSync();
        var items = new List<T>();
        while (await reader.ReadAsync(token).NoSync()) items.Add(LibrarianJson.Deserialize<T>(reader.GetString(0))!);
        return new LibrarianQueryResult<T> { Items = items, Index = path, IndexEntriesExamined = items.Count, DocumentsDeserialized = items.Count };
    }

    public ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100,
        CancellationToken cancellationToken = default)
    {
        string encoded = TursoIndexValue.Encode(value);
        return Indexed<T>(fieldPath, encoded, encoded, false, skip, take, cancellationToken);
    }

    public ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        string? min = minimum is null ? null : TursoIndexValue.Encode(minimum);
        string? max = maximum is null ? null : TursoIndexValue.Encode(maximum);
        if (min is not null && max is not null && (min[0] != max[0] || string.CompareOrdinal(min, max) > 0)) throw new ArgumentException("Range bounds must have the same scalar type and be ordered.");
        return Indexed<T>(fieldPath, min, max, descending, skip, take, cancellationToken);
    }

    private async ValueTask<object> Aggregate(string path, object? value, bool exists, CancellationToken token)
    {
        Check();
        await using DbConnection connection = await _database.Open(token).NoSync();
        await RequireIndex(connection, path, token).NoSync();
        var from = "FROM librarian_turso_values WHERE database_key=$1 AND container=$2 AND path=$3 AND value=$4";
        await using DbCommand command = Command(connection,
            exists ? "SELECT EXISTS(SELECT 1 " + from + ")" : "SELECT COUNT(*) " + from, path, TursoIndexValue.Encode(value));
        return (await command.ExecuteScalarAsync(token).NoSync())!;
    }

    public async ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) =>
        checked((int)(long)await Aggregate(fieldPath, value, false, cancellationToken).NoSync());

    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) =>
        Convert.ToInt64(await Aggregate(fieldPath, value, true, cancellationToken).NoSync()) != 0;
}
