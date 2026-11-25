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
            local p = (i - 3) * 2 + 1
            if ARGV[p] == '0' then
                if value ~= false then return {i - 2} end
            elseif value ~= ARGV[p + 1] then
                return {i - 2}
            end
        end
        local paths = redis.call('SMEMBERS', KEYS[2])
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
        if (writes.Count == 0)
        {
            lock (commands) AddDocumentConditions();
            return true;
        }
        var keys = new RedisKey[conditions.Count + 2];
        keys[0] = Version;
        keys[1] = Schema;
        var arguments = new RedisValue[conditions.Count * 2];
        for (var i = 0; i < conditions.Count; i++)
        {
            keys[i + 2] = BatchDocument(conditions[i].Id);
            string? expected = conditions[i].ExpectedValue;
            arguments[i * 2] = expected is null ? "0" : "1";
            arguments[i * 2 + 1] = expected ?? "";
        }
        RedisResult snapshot = await store.ScriptEvaluateAsync(PrepareBatchScript, keys, arguments).WaitAsync(token).NoSync();
        var parts = (RedisResult[])snapshot!;
        if (parts.Length == 1)
        {
            RedisLibrarianDatabase.RecordConditionConflict(conditions[(int)parts[0] - 1]);
            return false;
        }
        var version = (RedisValue)parts[0];
        var paths = (string[])parts[1]!;
        lock (commands)
        {
            if (paths.Length == 0)
            {
                // No index delta depends on old rows. Fence the schema and the exact document
                // conditions so independent writes can commit during preparation.
                commands.UnindexedWrite(Schema, Version);
                AddDocumentConditions();
            }
            else commands.Version(Version, version, changed: true);
        }
        await PrepareWrites(store, writes, paths, commands, token).NoSync();
        return true;

        void AddDocumentConditions()
        {
            foreach (LibrarianCondition condition in conditions)
                commands.Condition(BatchDocument(condition.Id), condition.ExpectedValue);
        }
    }

    private async ValueTask PrepareWrites(IDatabase store, IReadOnlyList<LibrarianWrite> writes, string[] paths,
        RedisBatchCommands commands, CancellationToken token, string? revision = null)
    {
        var documentKeys = new RedisKey[writes.Count];
        var ids = new string[writes.Count];
        for (var i = 0; i < writes.Count; i++) documentKeys[i] = Document(ids[i] = Id(writes[i].Id));
        var fields = new RedisValue[paths.Length];
        var sortFields = new string[paths.Length];
        for (var i = 0; i < paths.Length; i++) { fields[i] = Field(paths[i]); sortFields[i] = SortField(paths[i]); }
        RedisResult[] previous = paths.Length == 0 ? [] : (RedisResult[])(await store.ScriptEvaluateAsync(
            ReadIndexFieldsScript, documentKeys, fields).WaitAsync(token).NoSync())!;
        // A batch pipelines independent container reads. Append this container's commands in write order.
        lock (commands)
        {
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
                commands.Write(documentKeys[i], Ids, id, write.Id, write.Value, revision);
            }
        }
    }
}
