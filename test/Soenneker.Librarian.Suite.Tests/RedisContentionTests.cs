using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Redis;
using StackExchange.Redis;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel("Redis")]
public class RedisContentionTests
{
    [Test]
    public async ValueTask Indexed_read_conflicts_are_bounded_and_the_container_recovers()
    {
        await using var fixture = new RedisPersistenceFixture();
        IDatabase real = await fixture.GetStore();
        ILibrarianContainer writer = await fixture.Database.GetContainer("items");
        await writer.AddItem("one", "{\"name\":\"test\"}");
        await writer.EnsureIndex("name");
        IDatabase wrapped = DispatchProxy.Create<IDatabase, ReadProxy>();
        var proxy = (ReadProxy)wrapped;
        proxy.Inner = real;
        await using var database = new RedisLibrarianDatabase(fixture.Key, _ => ValueTask.FromResult(wrapped));
        ILibrarianContainer reader = await database.GetContainer("items");
        int conflicts = 0;
        proxy.AfterRange = async () =>
        {
            conflicts++;
            await writer.UpdateItemStrict("one", "{\"name\":\"test\",\"amount\":" + conflicts + "}");
        };
        try
        {
            await reader.FindByIndex<RedisRow>("name", "test").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception("Continuously changing index returned an inconsistent snapshot.");
        }
        catch (TimeoutException ex) when (ex.Message.Contains("index repeatedly changed", StringComparison.Ordinal)) { }
        if (conflicts != 8) throw new Exception("Conflict retry budget was not enforced.");
        proxy.AfterRange = null;
        if ((await reader.FindByIndex<RedisRow>("name", "test")).Items.Count != 1)
            throw new Exception("Conflict left the container unusable.");
    }

    [Test]
    public async ValueTask Cancellation_interrupts_a_stalled_index_read()
    {
        await using var fixture = new RedisPersistenceFixture();
        ILibrarianContainer writer = await fixture.Database.GetContainer("items");
        await writer.AddItem("one", "{\"name\":\"test\"}");
        await writer.EnsureIndex("name");
        IDatabase wrapped = DispatchProxy.Create<IDatabase, ReadProxy>();
        var proxy = (ReadProxy)wrapped;
        proxy.Inner = await fixture.GetStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.AfterRange = async () => { entered.SetResult(); await release.Task; };
        await using var database = new RedisLibrarianDatabase(fixture.Key, _ => ValueTask.FromResult(wrapped));
        ILibrarianContainer reader = await database.GetContainer("items");
        using var stop = new CancellationTokenSource();
        Task reading = reader.FindByIndex<RedisRow>("name", "test", cancellationToken: stop.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stop.CancelAsync();
            try { await reading.WaitAsync(TimeSpan.FromSeconds(1)); throw new Exception("Read ignored cancellation."); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        finally { release.TrySetResult(); }
    }

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
}
