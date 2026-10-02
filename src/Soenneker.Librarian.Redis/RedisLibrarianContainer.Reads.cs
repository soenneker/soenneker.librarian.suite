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
    private const string CountIndexScript = """
        if redis.call('SISMEMBER', KEYS[1], ARGV[1]) == 0 then return -1 end
        return redis.call('ZLEXCOUNT', KEYS[2], ARGV[2], ARGV[3])
        """;

    private async ValueTask<int> CountIndex(string path, string minimum, string maximum, CancellationToken token)
    {
        RedisIndexValue.ValidatePath(path);
        IDatabase store = await Store(token).NoSync();
        long count = (long)await store.ScriptEvaluateAsync(CountIndexScript, [Schema, Index(path)], [path, minimum, maximum])
            .WaitAsync(token).NoSync();
        if (count < 0) throw new InvalidOperationException($"Index '{path}' does not exist.");
        return checked((int)count);
    }

    private const string IndexVersionScript = """
        if redis.call('SISMEMBER', KEYS[1], ARGV[1]) == 0 then return false end
        return { redis.call('GET', KEYS[2]) }
        """;

    private const string ReadIndexedItemsScript = """
        local version = redis.call('GET', KEYS[1])
        if ARGV[1] == '0' then
            if version ~= false then return false end
        elseif version ~= ARGV[2] then
            return false
        end
        local result = {}
        for i = 2, #KEYS do result[i - 1] = redis.call('HGET', KEYS[i], 'json') end
        return result
        """;

    public async ValueTask<int> CountRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null,
        CancellationToken cancellationToken = default)
    {
        string? min = minimum is null ? null : RedisIndexValue.Encode(minimum);
        string? max = maximum is null ? null : RedisIndexValue.Encode(maximum);
        if (min is not null && max is not null && (min[0] != max[0] || string.CompareOrdinal(min, max) > 0))
            throw new ArgumentException("Range bounds must have the same scalar type and minimum must not exceed maximum.");
        return await CountIndex(fieldPath, min is null ? "-" : "[" + min + "!",
            max is null ? "+" : "[" + max + "!~", cancellationToken).NoSync();
    }

    private const string ReadItemsScript = """
        local result = {}
        for i = 1, #KEYS do
            result[i] = redis.call('HGET', KEYS[i], 'json')
        end
        return result
        """;

    public async ValueTask<string?[]> GetItems(IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        IDatabase store = await Store(cancellationToken).NoSync();
        if (ids.Count == 0) return [];
        var keys = new RedisKey[ids.Count];
        for (int i = 0; i < ids.Count; i++) keys[i] = Document(Id(ids[i]));
        cancellationToken.ThrowIfCancellationRequested();
        var values = (RedisResult[])(await store.ScriptEvaluateAsync(ReadItemsScript, keys).WaitAsync(cancellationToken).NoSync())!;
        var result = new string?[values.Length];
        for (int i = 0; i < values.Length; i++) result[i] = values[i].IsNull ? null : (string?)values[i];
        return result;
    }

    public async ValueTask<int> CountItems(CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken).NoSync();
        return checked((int)await store.SetLengthAsync(Ids).WaitAsync(cancellationToken).NoSync());
    }
}
