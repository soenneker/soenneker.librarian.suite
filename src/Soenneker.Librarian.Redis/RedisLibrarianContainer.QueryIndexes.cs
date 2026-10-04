using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianContainer
{
    private const string QueryIndexesScript = """
        local missing = {}
        for i = 2, #ARGV do
            if redis.call('SISMEMBER', KEYS[1], ARGV[i]) == 0 then missing[#missing + 1] = ARGV[i] end
        end
        local sorted = 1
        if ARGV[1] ~= '' then sorted = redis.call('SISMEMBER', KEYS[2], ARGV[1]) end
        return { missing, sorted }
        """;

    private async ValueTask EnsureQueryIndexes(IDatabase store, RedisQueryPlan plan, CancellationToken token)
    {
        if (plan.Paths.Count == 0) return;
        var arguments = new RedisValue[plan.Paths.Count + 1];
        arguments[0] = plan.Order ?? "";
        for (var i = 0; i < plan.Paths.Count; i++) arguments[i + 1] = plan.Paths[i];
        var result = (RedisResult[])(await store.ScriptEvaluateAsync(QueryIndexesScript, [Schema, SortSchema], arguments)
            .WaitAsync(token).NoSync())!;
        foreach (string path in (string[])result[0]!) await EnsureIndex(path, token).NoSync();
        if (plan.Order is not null && (long)result[1] == 0) await EnsureSortFields(store, plan.Order, token).NoSync();
    }
}
