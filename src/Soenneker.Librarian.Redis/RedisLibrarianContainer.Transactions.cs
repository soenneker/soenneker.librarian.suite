using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions.Transactions;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianContainer
{
    internal RedisKey BatchDocument(string id) => Document(Id(id));

    private const string PrepareBatchScript = """
        for i = 3, #KEYS do
            local value = redis.call('HGET', KEYS[i], 'json')
            local p = (i - 3) * 2 + 2
            if ARGV[p] == '0' then
                if value ~= false then return false end
            elseif value ~= ARGV[p + 1] then
                return false
            end
        end
        local paths = {}
        if ARGV[1] == '1' then paths = redis.call('SMEMBERS', KEYS[2]) end
        return { redis.call('GET', KEYS[1]), paths }
        """;

    private const string ReadIndexFieldsScript = """
        local result = {}
        for i = 1, #KEYS do
            result[i] = redis.call('HMGET', KEYS[i], unpack(ARGV))
        end
        return result
        """;

    internal async ValueTask<bool> PrepareBatch(IDatabase store, IReadOnlyList<LibrarianCondition> conditions,
        IReadOnlyList<LibrarianWrite> writes, RedisBatchCommands commands, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        var keys = new RedisKey[conditions.Count + 2];
        keys[0] = Version;
        keys[1] = Schema;
        var arguments = new RedisValue[conditions.Count * 2 + 1];
        arguments[0] = writes.Count == 0 ? "0" : "1";
        for (var i = 0; i < conditions.Count; i++)
        {
            keys[i + 2] = BatchDocument(conditions[i].Id);
            string? expected = conditions[i].ExpectedValue;
            arguments[i * 2 + 1] = expected is null ? "0" : "1";
            arguments[i * 2 + 2] = expected ?? "";
        }
        RedisResult snapshot = await store.ScriptEvaluateAsync(PrepareBatchScript, keys, arguments).WaitAsync(token).NoSync();
        if (snapshot.IsNull) return false;
        var parts = (RedisResult[])snapshot!;
        var version = (RedisValue)parts[0];
        var paths = (string[])parts[1]!;
        commands.Version(Version, version, writes.Count != 0);
        if (writes.Count == 0) return true;
        await PrepareWrites(store, writes, paths, commands, token).NoSync();
        return true;
    }

    private async ValueTask PrepareWrites(IDatabase store, IReadOnlyList<LibrarianWrite> writes, string[] paths,
        RedisBatchCommands commands, CancellationToken token)
    {
        var documentKeys = new RedisKey[writes.Count];
        var ids = new string[writes.Count];
        for (var i = 0; i < writes.Count; i++) documentKeys[i] = Document(ids[i] = Id(writes[i].Id));
        var fields = new RedisValue[paths.Length];
        var sortFields = new string[paths.Length];
        for (var i = 0; i < paths.Length; i++) { fields[i] = Field(paths[i]); sortFields[i] = SortField(paths[i]); }
        RedisResult[] previous = paths.Length == 0 ? [] : (RedisResult[])(await store.ScriptEvaluateAsync(
            ReadIndexFieldsScript, documentKeys, fields).WaitAsync(token).NoSync())!;
        for (var i = 0; i < writes.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            LibrarianWrite write = writes[i];
            string id = ids[i];
            using JsonDocument? json = write.Value is not null && paths.Length > 0 ? JsonDocument.Parse(write.Value) : null;
            RedisResult[] oldValues = paths.Length == 0 ? [] : (RedisResult[])previous[i]!;
            for (var j = 0; j < paths.Length; j++)
            {
                string path = paths[j];
                string? old = oldValues[j].IsNull ? null : (string?)oldValues[j];
                string? next = json is null ? null : RedisIndexValue.Read(json.RootElement, path);
                if (string.Equals(old, next, StringComparison.Ordinal)) continue;
                commands.Index(documentKeys[i], Index(path), Distinct(path), Present(path),
                    old is null ? default : Bucket(path, old), next is null ? default : Bucket(path, next),
                    fields[j].ToString(), sortFields[j], id, old, next);
            }
            commands.Write(documentKeys[i], Ids, id, write.Id, write.Value);
        }
    }
}
