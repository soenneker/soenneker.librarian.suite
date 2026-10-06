using System;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianContainer
{
    private const string VersionedSnapshotScript = """
        local json = redis.call('HGET', KEYS[3], 'json')
        if json == false then return false end
        redis.call('HSETNX', KEYS[3], 'revision', ARGV[1])
        return { redis.call('GET', KEYS[1]), redis.call('SMEMBERS', KEYS[2]), json, redis.call('HGET', KEYS[3], 'revision') }
        """;

    public async ValueTask<LibrarianItem<string>?> GetItemWithVersion(string id, CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        RedisResult value = await store.ScriptEvaluateAsync(VersionedSnapshotScript,
            [Version, Schema, Document(Id(id))], [Guid.NewGuid().ToString()]).WaitAsync(cancellationToken);
        if (value.IsNull) return null;
        var parts = (RedisResult[])value!;
        return new LibrarianItem<string>((string)parts[2]!, (string)parts[3]!);
    }

    public async ValueTask<LibrarianItem<string>?> UpdateItemIfVersion(string id, string document, string version,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        string next = Guid.NewGuid().ToString();
        return await WriteVersion(id, document, version, next, cancellationToken)
            ? new LibrarianItem<string>(document, next) : null;
    }

    public ValueTask<bool> DeleteItemIfVersion(string id, string version, CancellationToken cancellationToken = default) =>
        WriteVersion(id, null, version, Guid.NewGuid().ToString(), cancellationToken);

    private async ValueTask<bool> WriteVersion(string id, string? document, string expected, string next, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expected);
        IDatabase store = await Store(token);
        RedisKey[] keys = [Version, Schema, Document(Id(id))];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            RedisResult value = await store.ScriptEvaluateAsync(VersionedSnapshotScript, keys, [Guid.NewGuid().ToString()]).WaitAsync(token);
            if (value.IsNull) return false;
            var parts = (RedisResult[])value!;
            if ((string?)parts[3] != expected) return false;
            var commands = new RedisBatchCommands();
            commands.Version(Version, (RedisValue)parts[0], changed: true);
            await PrepareWrites(store, [new LibrarianWrite("unused", id, document)], (string[])parts[1]!, commands, token, next);
            token.ThrowIfCancellationRequested();
            // Never cancel the wait after dispatch: a lost response is an uncertain commit, not a conflict.
            if ((long)await commands.Execute(store) == 1) return true;
        }
    }
}
