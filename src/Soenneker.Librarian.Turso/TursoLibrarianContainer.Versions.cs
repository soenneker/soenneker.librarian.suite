using System;
using System.Threading;
using System.Threading.Tasks;
using Turso;
using System.Data.Common;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Turso;

internal sealed partial class TursoLibrarianContainer
{
    internal async ValueTask<LibrarianItem<string>?> ReadVersion(DbConnection connection, string id, CancellationToken token)
    {
        await using DbCommand command = Command(connection,
            "SELECT document,revision FROM librarian_turso_documents WHERE database_key=$1 AND container=$2 AND id_key=$3", Id(id));
        await using DbDataReader reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? new LibrarianItem<string>(reader.GetString(0), reader.GetString(1)) : null;
    }

    public async ValueTask<LibrarianItem<string>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default)
    {
        Check();
        await using DbConnection connection = await _database.Open(cancellationToken);
        return await ReadVersion(connection, id, cancellationToken);
    }

    public async ValueTask<LibrarianItem<string>?> UpdateItemIfVersion(string id, string document, string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Check();
        await using DbConnection connection = await _database.Open(cancellationToken);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
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
        await using DbConnection connection = await _database.Open(cancellationToken);
        await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
        await _database.LockWrites(connection, cancellationToken);
        LibrarianItem<string>? current = await ReadVersion(connection, id, cancellationToken);
        if (current is null || current.Version != version) return false;
        await Write(connection, id, null, "upsert", cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
