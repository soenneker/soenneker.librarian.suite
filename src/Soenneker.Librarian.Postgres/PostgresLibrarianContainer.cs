using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Soenneker.Atomics.ValueBools;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Postgres;

public sealed partial class PostgresLibrarianContainer : ILibrarianContainer
{
    private readonly PostgresLibrarianDatabase _database;
    private readonly string _name;
    private ValueAtomicBool _disposed = new(false);

    internal PostgresLibrarianContainer(string name, PostgresLibrarianDatabase database)
    {
        _database = database;
        _name = PostgresIndexValue.Hex(name);
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        _database.Check();
    }

    private static string Id(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return PostgresIndexValue.Hex(id.ToUpperInvariant());
    }

    private NpgsqlCommand Command(NpgsqlConnection connection, string sql, params object[] values) =>
        _database.Command(connection, sql, [_database.Key, _name, .. values]);

    internal async ValueTask<string?> Read(NpgsqlConnection connection, string id, CancellationToken token)
    {
        await using NpgsqlCommand command = Command(connection,
            "SELECT document FROM public.librarian_postgres_documents WHERE database_key=$1 AND container=$2 AND id_key=$3", Id(id));
        return (string?)await command.ExecuteScalarAsync(token).NoSync();
    }

    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).NoSync();
        return await Read(connection, id, cancellationToken).NoSync();
    }

    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).NoSync() ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    private async ValueTask<List<string>> Paths(NpgsqlConnection connection, CancellationToken token)
    {
        await using NpgsqlCommand command = Command(connection,
            "SELECT path FROM public.librarian_postgres_indexes WHERE database_key=$1 AND container=$2");
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token).NoSync();
        var paths = new List<string>();
        while (await reader.ReadAsync(token).NoSync()) paths.Add(reader.GetString(0));
        return paths;
    }

    // The caller owns a transaction and the logical database write lock. Failure rolls back documents and indexes together.
    internal async ValueTask<bool> Write(NpgsqlConnection connection, string id, string? document, string mode, CancellationToken token)
    {
        string normalized = Id(id);
        if (mode != "upsert")
        {
            string? existing = await Read(connection, id, token).NoSync();
            if (mode == "add" && existing is not null) throw new InvalidOperationException($"Document '{id}' already exists.");
            if (mode == "update" && existing is null) return false;
        }
        if (document is null)
        {
            await using NpgsqlCommand delete = Command(connection,
                "DELETE FROM public.librarian_postgres_documents WHERE database_key=$1 AND container=$2 AND id_key=$3", normalized);
            await delete.ExecuteNonQueryAsync(token).NoSync();
            return true;
        }

        List<string> paths = await Paths(connection, token).NoSync();
        using JsonDocument? json = paths.Count > 0 ? JsonDocument.Parse(document) : null;
        var entries = new List<(string Path, string Value)>();
        foreach (string path in paths)
        {
            string? value = PostgresIndexValue.Read(json!.RootElement, path);
            if (value is not null) entries.Add((path, value));
        }
        await using (NpgsqlCommand command = Command(connection, """
            INSERT INTO public.librarian_postgres_documents (database_key,container,id_key,original_id,document,body)
            VALUES ($1,$2,$3,$4,$5,(CASE WHEN pg_input_is_valid($5,'jsonb') THEN $5 ELSE NULL END)::jsonb)
            ON CONFLICT (database_key,container,id_key) DO UPDATE SET document=EXCLUDED.document,body=EXCLUDED.body
            """, normalized, id, document))
            await command.ExecuteNonQueryAsync(token).NoSync();
        await using (NpgsqlCommand command = Command(connection,
            "DELETE FROM public.librarian_postgres_values WHERE database_key=$1 AND container=$2 AND id_key=$3", normalized))
            await command.ExecuteNonQueryAsync(token).NoSync();
        foreach ((string path, string value) in entries)
            await InsertValue(connection, normalized, path, value, token).NoSync();
        return true;
    }

    private async ValueTask InsertValue(NpgsqlConnection connection, string id, string path, string value, CancellationToken token)
    {
        await using NpgsqlCommand command = Command(connection,
            "INSERT INTO public.librarian_postgres_values (database_key,container,id_key,path,value) VALUES ($1,$2,$3,$4,$5)", id, path, value);
        await command.ExecuteNonQueryAsync(token).NoSync();
    }

    private async ValueTask<bool> Mutate(string id, string? document, string mode, CancellationToken token)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(token).NoSync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token).NoSync();
        await _database.LockWrites(connection, token).NoSync();
        bool result = await Write(connection, id, document, mode, token).NoSync();
        token.ThrowIfCancellationRequested();
        await transaction.CommitAsync(token).NoSync();
        return result;
    }

    public async ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await Mutate(id, document, "add", cancellationToken).NoSync();
        return document;
    }

    public async ValueTask<string?> UpdateItem(string id, string document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return await Mutate(id, document, "update", cancellationToken).NoSync() ? document : null;
    }

    public async ValueTask<string> UpdateItemStrict(string id, string document, CancellationToken cancellationToken = default) =>
        await UpdateItem(id, document, cancellationToken).NoSync() ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default) =>
        _ = await Mutate(id, null, "upsert", cancellationToken).NoSync();

    public async ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).NoSync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).NoSync();
        await _database.LockWrites(connection, cancellationToken).NoSync();
        await using NpgsqlCommand command = Command(connection,
            "DELETE FROM public.librarian_postgres_documents WHERE database_key=$1 AND container=$2");
        await command.ExecuteNonQueryAsync(cancellationToken).NoSync();
        await transaction.CommitAsync(cancellationToken).NoSync();
    }

    public async ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).NoSync();
        await using NpgsqlCommand command = Command(connection,
            "SELECT original_id,document FROM public.librarian_postgres_documents WHERE database_key=$1 AND container=$2 ORDER BY id_key");
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).NoSync();
        var items = new List<IdValuePair>();
        while (await reader.ReadAsync(cancellationToken).NoSync())
            items.Add(new IdValuePair { Id = reader.GetString(0), Value = reader.GetString(1) });
        return items;
    }

    public async ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default) =>
        (await GetLibrarianItems(cancellationToken).NoSync()).Select(item => item.Value).ToList();

    public async ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).NoSync();
        await using NpgsqlCommand command = Command(connection,
            "SELECT original_id FROM public.librarian_postgres_documents WHERE database_key=$1 AND container=$2 ORDER BY id_key");
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).NoSync();
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken).NoSync()) ids.Add(reader.GetString(0));
        return ids;
    }

    public void Dispose() => _disposed.TrySetTrue();
}
