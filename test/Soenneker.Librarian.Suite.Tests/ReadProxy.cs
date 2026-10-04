using System;
using System.Reflection;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Soenneker.Librarian.Suite.Tests;

public class ReadProxy : DispatchProxy
{
    public IDatabase Inner = null!;
    public Func<Task>? AfterRange;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        object? result = method!.Invoke(Inner, args);
        return method.Name == "ExecuteAsync" && args?[0] is "ZRANGEBYLEX" ? After((Task<RedisResult>)result!) : result;
    }
    private async Task<RedisResult> After(Task<RedisResult> task)
    {
        RedisResult result = await task;
        if (AfterRange is { } callback) await callback();
        return result;
    }
}
