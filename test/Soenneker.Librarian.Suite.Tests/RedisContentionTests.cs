using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Redis;
using Soenneker.Librarian.Abstractions.Transactions;
using StackExchange.Redis;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel("Redis")]
public class RedisContentionTests
{
    [Test]
    public async ValueTask Indexed_read_conflicts_are_bounded_and_the_container_recovers(CancellationToken cancellationToken)
    {
        await using var fixture = new RedisPersistenceFixture();
        IDatabase real = await fixture.GetStore();
        ILibrarianContainer writer = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await writer.AddItem("one", "{\"name\":\"test\"}", cancellationToken: cancellationToken);
        await writer.EnsureIndex("name", cancellationToken: cancellationToken);
        IDatabase wrapped = DispatchProxy.Create<IDatabase, ReadProxy>();
        var proxy = (ReadProxy)wrapped;
        proxy.Inner = real;
        await using var database = new RedisLibrarianDatabase(fixture.Key, _ => ValueTask.FromResult(wrapped));
        ILibrarianContainer reader = await database.GetContainer("items", cancellationToken: cancellationToken);
        var conflicts = 0;
        proxy.AfterRange = async () =>
        {
            conflicts++;
            await writer.UpdateItemStrict("one", "{\"name\":\"test\",\"amount\":" + conflicts + "}", cancellationToken: cancellationToken);
        };
        try
        {
            await reader.FindByIndex<RedisRow>("name", "test", cancellationToken: cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);
            throw new Exception("Continuously changing index returned an inconsistent snapshot.");
        }
        catch (TimeoutException ex) when (ex.Message.Contains("index repeatedly changed", StringComparison.Ordinal)) { }
        if (conflicts != 8) throw new Exception("Conflict retry budget was not enforced.");
        proxy.AfterRange = null;
        if ((await reader.FindByIndex<RedisRow>("name", "test", cancellationToken: cancellationToken)).Items.Count != 1)
            throw new Exception("Conflict left the container unusable.");
    }

    [Test]
    public async Task PausedBatchAllowsIndependentCommitsAndUnloadingWaits(CancellationToken ct)
    {
        await using var fixture = new RedisPersistenceFixture();
        IDatabase wrapped = DispatchProxy.Create<IDatabase, ReadProxy>();
        var proxy = (ReadProxy)wrapped;
        proxy.Inner = await fixture.GetStore();
        await using var database = new RedisLibrarianDatabase(fixture.Key, _ => ValueTask.FromResult(wrapped));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.AfterBatchPrepare = async () =>
        {
            proxy.AfterBatchPrepare = null;
            entered.SetResult();
            await release.Task;
        };
        Task<bool> slow = database.Execute(new LibrarianBatch([new LibrarianWrite("slow", "row", "one")]), ct).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            var peer = new LibrarianBatch([new LibrarianWrite("peer", "row", "two")]);
            if (!await database.Execute(peer, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(2), ct))
                throw new Exception("A separate batch could not commit.");
            Task<bool> unload = database.UnloadContainer("slow", ct).AsTask();
            if (unload.IsCompleted) throw new Exception("Unloading retired the handle during an active batch.");
            release.SetResult();
            if (!await slow || !await unload) throw new Exception("The batch or pending unload was lost.");
        }
        finally { release.TrySetResult(); }
    }

    [Test]
    public async Task BatchRechecksUnindexedConditionsAndNewIndexesAtCommit(CancellationToken ct)
    {
        await using var fixture = new RedisPersistenceFixture();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", ct);
        IDatabase wrapped = DispatchProxy.Create<IDatabase, ReadProxy>();
        var proxy = (ReadProxy)wrapped;
        proxy.Inner = await fixture.GetStore();
        await using var database = new RedisLibrarianDatabase(fixture.Key, _ => ValueTask.FromResult(wrapped));
        const string row = "{\"name\":\"value\"}";
        proxy.AfterBatchPrepare = async () =>
        {
            proxy.AfterBatchPrepare = null;
            await items.AddItem("guard", row, ct);
        };
        var conditional = new LibrarianBatch([new LibrarianWrite("items", "item", row)],
            [new LibrarianCondition("items", "guard", null)]);
        if (await database.Execute(conditional, ct) || await items.GetItem("item", ct) != null)
            throw new Exception("A concurrent write bypassed the unindexed document condition.");

        proxy.AfterBatchPrepare = async () =>
        {
            proxy.AfterBatchPrepare = null;
            await items.EnsureIndex("name", ct);
        };
        if (!await database.Execute(new LibrarianBatch([new LibrarianWrite("items", "item", row)]), ct) ||
            await items.CountByIndex("name", "value", ct) != 2)
            throw new Exception("The batch committed without maintaining the newly installed index.");
    }

    [Test]
    public async ValueTask Cancellation_interrupts_a_stalled_index_read(CancellationToken cancellationToken)
    {
        await using var fixture = new RedisPersistenceFixture();
        ILibrarianContainer writer = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await writer.AddItem("one", "{\"name\":\"test\"}", cancellationToken: cancellationToken);
        await writer.EnsureIndex("name", cancellationToken: cancellationToken);
        IDatabase wrapped = DispatchProxy.Create<IDatabase, ReadProxy>();
        var proxy = (ReadProxy)wrapped;
        proxy.Inner = await fixture.GetStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.AfterRange = async () => { entered.SetResult(); await release.Task; };
        await using var database = new RedisLibrarianDatabase(fixture.Key, _ => ValueTask.FromResult(wrapped));
        ILibrarianContainer reader = await database.GetContainer("items", cancellationToken: cancellationToken);
        using var stop = new CancellationTokenSource();
        Task reading = reader.FindByIndex<RedisRow>("name", "test", cancellationToken: stop.Token).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken: cancellationToken);
            await stop.CancelAsync();
            try { await reading.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken: cancellationToken); throw new Exception("Read ignored cancellation."); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        finally { release.TrySetResult(); }
    }
}
