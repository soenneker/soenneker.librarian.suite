using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianContainer
{
    private const string MembersSnapshotScript = """
        return { redis.call('GET', KEYS[1]), redis.call('SMEMBERS', KEYS[2]) }
        """;

    private const string FieldsSnapshotScript = """
        local version = redis.call('GET', KEYS[1])
        if ARGV[1] == '0' then
            if version ~= false then return false end
        elseif version ~= ARGV[2] then
            return false
        end
        local result = {}
        for i = 2, #KEYS do
            for j = 3, #ARGV do
                result[#result + 1] = redis.call('HGET', KEYS[i], ARGV[j])
            end
        end
        return result
        """;

    // Every document key is declared before execution; the version guard joins bounded reads
    // into one snapshot without depending on a server-wide Lua lock or SORT GET patterns.
    private async ValueTask<RedisValue[]?> ReadFields(IDatabase store, RedisValue version, IReadOnlyList<string> ids,
        string[] fields, CancellationToken token)
    {
        var values = new RedisValue[checked(ids.Count * fields.Length)];
        var arguments = new RedisValue[fields.Length + 2];
        arguments[0] = version.IsNull ? "0" : "1";
        arguments[1] = version.IsNull ? "" : version;
        for (var i = 0; i < fields.Length; i++) arguments[i + 2] = fields[i];
        for (var start = 0; start < ids.Count; start += 128)
        {
            token.ThrowIfCancellationRequested();
            int length = Math.Min(128, ids.Count - start);
            var keys = new RedisKey[length + 1];
            keys[0] = Version;
            for (var i = 0; i < length; i++) keys[i + 1] = Document(ids[start + i]);
            RedisResult result = await store.ScriptEvaluateAsync(FieldsSnapshotScript, keys, arguments).WaitAsync(token).NoSync();
            if (result.IsNull) return null;
            var batch = (RedisResult[])result!;
            for (var i = 0; i < batch.Length; i++) values[start * fields.Length + i] = (RedisValue)batch[i];
        }
        return values;
    }

    private async ValueTask<RedisValue[]> ReadSetFields(IDatabase store, RedisKey set, string[] fields, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            var snapshot = (RedisResult[])(await store.ScriptEvaluateAsync(MembersSnapshotScript, [Version, set])
                .WaitAsync(token).NoSync())!;
            var ids = (string[])snapshot[1]!;
            Array.Sort(ids, StringComparer.Ordinal);
            var storedFields = new List<string>();
            foreach (string field in fields) if (field != "#") storedFields.Add(field);
            RedisValue[]? values = await ReadFields(store, (RedisValue)snapshot[0], ids, storedFields.ToArray(), token).NoSync();
            if (values is null) { await RetryIndexConflict(attempt, token).NoSync(); continue; }
            var result = new RedisValue[checked(ids.Length * fields.Length)];
            var source = 0;
            for (var i = 0; i < ids.Length; i++)
                for (var j = 0; j < fields.Length; j++)
                    result[i * fields.Length + j] = fields[j] == "#" ? ids[i] : values[source++];
            return result;
        }
    }
}
