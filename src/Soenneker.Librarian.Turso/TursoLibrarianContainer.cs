using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Turso;
using System.Data.Common;
using Soenneker.Atomics.ValueBools;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Turso;

internal sealed partial class TursoLibrarianContainer : ILibrarianContainer
{
    private readonly TursoLibrarianDatabase _database;
    private readonly string _name;
    private ValueAtomicBool _disposed = new(false);

    internal TursoLibrarianContainer(string name, TursoLibrarianDatabase database)
    {
        _database = database;
        _name = TursoIndexValue.Hex(name);
    }

    private void Check()
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        _database.Check();
    }

    private static string Id(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return TursoIndexValue.Hex(id.ToUpperInvariant());
    }

    private DbCommand Command(DbConnection connection, string sql, params ReadOnlySpan<object> values)
    {
        DbCommand command = _database.Command(connection, sql, _database.Key, _name);
        foreach (object value in values) command.Parameters.Add(new TursoParameter("$" + (command.Parameters.Count + 1), value));
        return command;
    }

    internal async ValueTask<string?> Read(DbConnection connection, string id, CancellationToken token)
    {
        await using DbCommand command = Command(connection,
            "SELECT document FROM librarian_turso_documents WHERE database_key=$1 AND container=$2 AND id_key=$3", Id(id));
        return (string?)await command.ExecuteScalarAsync(token).NoSync();
    }

    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default)
    {
        Check();
        await using DbConnection connection = await _database.Open(cancellationToken).NoSync();
        return await Read(connection, id, cancellationToken).NoSync();
    }

    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).NoSync() ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    private async ValueTask<List<string>> Paths(DbConnection connection, CancellationToken token)
    {
        await using DbCommand command = Command(connection,
            "SELECT path FROM librarian_turso_indexes WHERE database_key=$1 AND container=$2");
        await using DbDataReader reader = await command.ExecuteReaderAsync(token).NoSync();
        var paths = new List<string>();
        while (await reader.ReadAsync(token).NoSync()) paths.Add(reader.GetString(0));
        return paths;
    }

    // The caller owns a transaction and the logical database write lock. Failure rolls back documents and indexes together.
    internal async ValueTask<bool> Write(DbConnection connection, string id, string? document, string mode, CancellationToken token)
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
            await using (DbCommand clear = Command(connection, "DELETE FROM librarian_turso_values WHERE database_key=$1 AND container=$2 AND id_key=$3", normalized))
                await clear.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            await using DbCommand delete = Command(connection,
                "DELETE FROM librarian_turso_documents WHERE database_key=$1 AND container=$2 AND id_key=$3", normalized);
            await delete.ExecuteNonQueryAsync(token).NoSync();
            return true;
        }

        List<string> paths = await Paths(connection, token).NoSync();
        using JsonDocument? json = paths.Count > 0 ? JsonDocument.Parse(document) : null;
        var entries = new List<(string Path, string Value)>();
        foreach (string path in paths)
        {
            string? value = TursoIndexValue.Read(json!.RootElement, path);
            if (value is not null) entries.Add((path, value));
        }
        await using (DbCommand command = Command(connection, """
            INSERT INTO librarian_turso_documents (database_key,container,id_key,original_id,document,revision)
            VALUES ($1,$2,$3,$4,$5,$6)
            ON CONFLICT (database_key,container,id_key) DO UPDATE SET document=EXCLUDED.document,revision=EXCLUDED.revision
            """, normalized, id, document, Guid.NewGuid().ToString("N")))
            await command.ExecuteNonQueryAsync(token).NoSync();
        await using (DbCommand command = Command(connection,
            "DELETE FROM librarian_turso_values WHERE database_key=$1 AND container=$2 AND id_key=$3", normalized))
            await command.ExecuteNonQueryAsync(token).NoSync();
        foreach ((string path, string value) in entries)
            await InsertValue(connection, normalized, path, value, token).NoSync();
        return true;
    }

    private async ValueTask InsertValue(DbConnection connection, string id, string path, string value, CancellationToken token)
    {
        await using DbCommand command = Command(connection,
            "INSERT INTO librarian_turso_values (database_key,container,id_key,path,value) VALUES ($1,$2,$3,$4,$5)", id, path, value);
        await command.ExecuteNonQueryAsync(token).NoSync();
    }

    private async ValueTask<bool> Mutate(string id, string? document, string mode, CancellationToken token)
    {
        Check();
        await using DbConnection connection = await _database.Open(token).NoSync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync(token).NoSync();
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
        await using DbConnection connection = await _database.Open(cancellationToken).NoSync();
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).NoSync();
        await _database.LockWrites(connection, cancellationToken).NoSync();
        await using (DbCommand clear = Command(connection, "DELETE FROM librarian_turso_values WHERE database_key=$1 AND container=$2"))
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = Command(connection,
            "DELETE FROM librarian_turso_documents WHERE database_key=$1 AND container=$2");
        await command.ExecuteNonQueryAsync(cancellationToken).NoSync();
        await transaction.CommitAsync(cancellationToken).NoSync();
    }

    public async ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default)
    {
        Check();
        await using DbConnection connection = await _database.Open(cancellationToken).NoSync();
        await using DbCommand command = Command(connection,
            "SELECT original_id,document FROM librarian_turso_documents WHERE database_key=$1 AND container=$2 ORDER BY id_key");
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).NoSync();
        var items = new List<IdValuePair>();
        while (await reader.ReadAsync(cancellationToken).NoSync())
            items.Add(new IdValuePair { Id = reader.GetString(0), Value = reader.GetString(1) });
        return items;
    }

    public async ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default)
    {
        Check();
        await using DbConnection connection = await _database.Open(cancellationToken).NoSync();
        await using DbCommand command = Command(connection,
            "SELECT document FROM librarian_turso_documents WHERE database_key=$1 AND container=$2 ORDER BY id_key");
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).NoSync();
        var items = new List<string>();
        while (await reader.ReadAsync(cancellationToken).NoSync()) items.Add(reader.GetString(0));
        return items;
    }

    public async ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default)
    {
        Check();
        await using DbConnection connection = await _database.Open(cancellationToken).NoSync();
        await using DbCommand command = Command(connection,
            "SELECT original_id FROM librarian_turso_documents WHERE database_key=$1 AND container=$2 ORDER BY id_key");
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).NoSync();
        var ids = new List<string>();
        while (await reader.ReadAsync(cancellationToken).NoSync()) ids.Add(reader.GetString(0));
        return ids;
    }

    public IQueryable<T> BuildQueryable<T>() { Check(); return new Soenneker.Librarian.Abstractions.Queries.LibrarianQueryable<T>(new TursoQueryProvider<T>(this)); }

    public async ValueTask<int> CountItems(CancellationToken cancellationToken = default)
    {
        Check();
        await using DbConnection connection = await _database.Open(cancellationToken).ConfigureAwait(false);
        await using DbCommand command = Command(connection, "SELECT COUNT(*) FROM librarian_turso_documents WHERE database_key=$1 AND container=$2");
        return checked((int)Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }

    public void Dispose() => _disposed.TrySetTrue();
}
