using System;
using System.Reflection;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Soenneker.Librarian.Suite.Tests;

public class ReadProxy : DispatchProxy
{
    public IDatabase Inner = null!;
    public Func<Task>? AfterRange;
    public Func<Task>? AfterSnapshot;
    public Func<Task>? AfterFields;
    public Func<Task>? AfterBatchPrepare;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        object? result = method!.Invoke(Inner, args);
        if (method.Name == "ExecuteAsync" && args?[0] is "ZRANGEBYLEX")
            return After((Task<RedisResult>)result!, AfterRange);
        if (method.Name == "ScriptEvaluateAsync" && args?[0] is string script)
        {
            if (script.Contains("local paths = redis.call('SMEMBERS', KEYS[2])", StringComparison.Ordinal))
                return After((Task<RedisResult>)result!, AfterBatchPrepare);
            if (script.Contains("local temporary = {}", StringComparison.Ordinal) ||
                script.Contains("local items = redis.call(ARGV[1]", StringComparison.Ordinal))
                return After((Task<RedisResult>)result!, AfterSnapshot);
            if (script.Contains("for j = 3, #ARGV do", StringComparison.Ordinal))
                return After((Task<RedisResult>)result!, AfterFields);
        }
        return result;
    }
    private static async Task<RedisResult> After(Task<RedisResult> task, Func<Task>? callback)
    {
        RedisResult result = await task;
        if (callback is not null) await callback();
        return result;
    }
}
