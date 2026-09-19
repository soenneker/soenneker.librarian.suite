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
    public async ValueTask<int> CountRangeByIndex(string fieldPath, object? minimum = null, object? maximum = null,
        CancellationToken cancellationToken = default)
    {
        string? min = minimum is null ? null : RedisIndexValue.Encode(minimum);
        string? max = maximum is null ? null : RedisIndexValue.Encode(maximum);
        if (min is not null && max is not null && (min[0] != max[0] || string.CompareOrdinal(min, max) > 0))
            throw new ArgumentException("Range bounds must have the same scalar type and minimum must not exceed maximum.");
        IDatabase store = await Store(cancellationToken).NoSync();
        await RequireIndex(store, fieldPath).NoSync();
        return checked((int)(long)await store.ExecuteAsync("ZLEXCOUNT", Index(fieldPath),
            min is null ? "-" : "[" + min + "!", max is null ? "+" : "[" + max + "!~").NoSync());
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
        var values = (RedisResult[])(await store.ScriptEvaluateAsync(ReadItemsScript, keys).NoSync())!;
        var result = new string?[values.Length];
        for (int i = 0; i < values.Length; i++) result[i] = values[i].IsNull ? null : (string?)values[i];
        return result;
    }

    public async ValueTask<int> CountItems(CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken).NoSync();
        return checked((int)await store.SetLengthAsync(Ids).NoSync());
    }
}
