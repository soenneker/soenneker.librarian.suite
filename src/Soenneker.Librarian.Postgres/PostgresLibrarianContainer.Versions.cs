using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Postgres;

public sealed partial class PostgresLibrarianContainer
{
    private async ValueTask<LibrarianItem<string>?> ReadVersion(NpgsqlConnection connection, string id, CancellationToken token)
    {
        await using NpgsqlCommand command = Command(connection,
            "SELECT document,revision FROM public.librarian_postgres_documents WHERE database_key=$1 AND container=$2 AND id_key=$3", Id(id));
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new LibrarianItem<string>(reader.GetString(0), reader.GetGuid(1).ToString()) : null;
    }

    public async ValueTask<LibrarianItem<string>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default)
    {
        Check();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken);
        return await ReadVersion(connection, id, cancellationToken);
    }

    public async ValueTask<LibrarianItem<string>?> UpdateItemIfVersion(string id, string document, string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Check();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await _database.LockWrites(connection, cancellationToken);
        LibrarianItem<string>? current = await ReadVersion(connection, id, cancellationToken);
        if (current is null || current.Version != version) return null;
        await Write(connection, id, document, "update", cancellationToken);
        LibrarianItem<string>? next = await ReadVersion(connection, id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return next;
    }

    public async ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Check();
        await using NpgsqlConnection connection = await _database.Open(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await _database.LockWrites(connection, cancellationToken);
        LibrarianItem<string>? current = await ReadVersion(connection, id, cancellationToken);
        if (current is null || current.Version != version) return false;
        await Write(connection, id, null, "upsert", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
