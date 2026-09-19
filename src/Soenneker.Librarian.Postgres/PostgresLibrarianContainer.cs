using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Postgres;

public sealed partial class PostgresLibrarianContainer : ILibrarianContainer
{
    private readonly PostgresLibrarianDatabase _database;
    private readonly string _name;
    private volatile bool _disposed;

    internal PostgresLibrarianContainer(string name, PostgresLibrarianDatabase database)
    {
        _database = database;
        _name = PostgresIndexValue.Hex(name);
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
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
        return (string?)await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).ConfigureAwait(false);
        return await Read(connection, id, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    private async ValueTask<List<string>> Paths(NpgsqlConnection connection, CancellationToken token)
    {
        await using NpgsqlCommand command = Command(connection,
            "SELECT path FROM public.librarian_postgres_indexes WHERE database_key=$1 AND container=$2");
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var paths = new List<string>();
        while (await reader.ReadAsync(token).ConfigureAwait(false)) paths.Add(reader.GetString(0));
        return paths;
    }

    // The caller owns a transaction and the logical database write lock. Failure rolls back documents and indexes together.
    internal async ValueTask<bool> Write(NpgsqlConnection connection, string id, string? document, string mode, CancellationToken token)
    {
        string normalized = Id(id);
        if (mode != "upsert")
        {
            string? existing = await Read(connection, id, token).ConfigureAwait(false);
            if (mode == "add" && existing is not null) throw new InvalidOperationException($"Document '{id}' already exists.");
            if (mode == "update" && existing is null) return false;
        }
        if (document is null)
        {
            await using NpgsqlCommand delete = Command(connection,
                "DELETE FROM public.librarian_postgres_documents WHERE database_key=$1 AND container=$2 AND id_key=$3", normalized);
            await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return true;
        }

        List<string> paths = await Paths(connection, token).ConfigureAwait(false);
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
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await using (NpgsqlCommand command = Command(connection,
            "DELETE FROM public.librarian_postgres_values WHERE database_key=$1 AND container=$2 AND id_key=$3", normalized))
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        foreach ((string path, string value) in entries)
            await InsertValue(connection, normalized, path, value, token).ConfigureAwait(false);
        return true;
    }

    private async ValueTask InsertValue(NpgsqlConnection connection, string id, string path, string value, CancellationToken token)
    {
        await using NpgsqlCommand command = Command(connection,
            "INSERT INTO public.librarian_postgres_values (database_key,container,id_key,path,value) VALUES ($1,$2,$3,$4,$5)", id, path, value);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async ValueTask<bool> Mutate(string id, string? document, string mode, CancellationToken token)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(token).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        await _database.LockWrites(connection, token).ConfigureAwait(false);
        bool result = await Write(connection, id, document, mode, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return result;
    }

    public async ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        await Mutate(id, document, "add", cancellationToken).ConfigureAwait(false);
        return document;
    }

    public async ValueTask<string?> UpdateItem(string id, string document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        return await Mutate(id, document, "update", cancellationToken).ConfigureAwait(false) ? document : null;
    }

    public async ValueTask<string> UpdateItemStrict(string id, string document, CancellationToken cancellationToken = default) =>
        await UpdateItem(id, document, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default) =>
        _ = await Mutate(id, null, "upsert", cancellationToken).ConfigureAwait(false);

    public async ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await _database.LockWrites(connection, cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = Command(connection,
            "DELETE FROM public.librarian_postgres_documents WHERE database_key=$1 AND container=$2");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = Command(connection,
            "SELECT original_id,document FROM public.librarian_postgres_documents WHERE database_key=$1 AND container=$2 ORDER BY id_key");
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<IdValuePair>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            items.Add(new IdValuePair { Id = reader.GetString(0), Value = reader.GetString(1) });
        return items;
    }

    public async ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default) =>
        (await GetLibrarianItems(cancellationToken).ConfigureAwait(false)).Select(item => item.Value).ToList();

    public async ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken).ConfigureAwait(false);
        await using NpgsqlCommand command = Command(connection,
            "SELECT original_id FROM public.librarian_postgres_documents WHERE database_key=$1 AND container=$2 ORDER BY id_key");
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ids.Add(reader.GetString(0));
        return ids;
    }

    public void Dispose() => _disposed = true;
}
