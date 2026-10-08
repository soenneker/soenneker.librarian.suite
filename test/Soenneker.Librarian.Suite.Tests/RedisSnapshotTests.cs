using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Redis;
using StackExchange.Redis;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel("Redis")]
public class RedisSnapshotTests
{
    [Test]
    public async Task Conditional_transactions_reject_competing_script_writes(CancellationToken cancellationToken)
    {
        await using var fixture = new RedisPersistenceFixture();
        IDatabase store = await fixture.GetStore();
        RedisKey version = fixture.RedisPrefix + "watch-version";
        for (var i = 0; i < 32; i++)
        {
            await store.StringSetAsync(version, i);
            ITransaction transaction = store.CreateTransaction();
            transaction.AddCondition(Condition.StringEqual(version, i));
            Task<long> pending = transaction.StringIncrementAsync(version);
            await store.ScriptEvaluateAsync("return redis.call('INCR', KEYS[1])", [version]);
            if (await transaction.ExecuteAsync()) throw new Exception("Stale transaction accepted a competing Lua write.");
            try { await pending; throw new Exception("Aborted transaction executed its increment."); }
            catch (TaskCanceledException) { }
            if (await store.StringGetAsync(version) != i + 1) throw new Exception("Aborted transaction changed the version.");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Ordered_pages_retry_when_documents_change_after_selection(bool sparse, CancellationToken cancellationToken)
    {
        await using var fixture = new RedisPersistenceFixture();
        ILibrarianContainer writer = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await fixture.Database.Execute(new LibrarianBatch(Enumerable.Range(0, 350).Select(i =>
            new LibrarianWrite("items", $"row-{i:D4}", $"{{\"name\":\"row-{i:D4}\",\"amount\":{i}}}"))), cancellationToken: cancellationToken);
        await writer.EnsureIndex("amount", cancellationToken: cancellationToken);
        await writer.EnsureIndex("name", cancellationToken: cancellationToken);
        IDatabase wrapped = DispatchProxy.Create<IDatabase, ReadProxy>();
        var proxy = (ReadProxy)wrapped;
        proxy.Inner = await fixture.GetStore();
        await using var database = new RedisLibrarianDatabase(fixture.Key, _ => ValueTask.FromResult(wrapped));
        ILibrarianContainer reader = await database.GetContainer("items", cancellationToken: cancellationToken);
        var snapshots = 0;
        proxy.AfterSnapshot = async () =>
        {
            if (++snapshots == 1)
                await writer.UpdateItemStrict("row-0000", "{\"name\":\"row-0000\",\"amount\":999}", cancellationToken: cancellationToken);
        };
        IQueryable<RedisRow> query = reader.BuildQueryable<RedisRow>();
        if (sparse) query = query.Where(row => row.Name == "row-0000" || row.Name == "row-0001");
        RedisRow[] rows = await query.OrderBy(row => row.Amount).Take(2).ToArrayAsync(cancellationToken: cancellationToken);
        string[] expected = sparse ? ["row-0001", "row-0000"] : ["row-0001", "row-0002"];
        if (snapshots != 2 || !rows.Select(row => row.Name).SequenceEqual(expected))
            throw new Exception("Page mixed selection and document versions.");
    }

    [Test]
    public async Task Collection_reads_retry_across_document_windows(CancellationToken cancellationToken)
    {
        await using var fixture = new RedisPersistenceFixture();
        ILibrarianContainer writer = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        LibrarianWrite[] Writes(string value) => Enumerable.Range(0, 260)
            .Select(i => new LibrarianWrite("items", $"row-{i:D4}", value)).ToArray();
        await fixture.Database.Execute(new LibrarianBatch(Writes("before")), cancellationToken: cancellationToken);
        IDatabase wrapped = DispatchProxy.Create<IDatabase, ReadProxy>();
        var proxy = (ReadProxy)wrapped;
        proxy.Inner = await fixture.GetStore();
        await using var database = new RedisLibrarianDatabase(fixture.Key, _ => ValueTask.FromResult(wrapped));
        ILibrarianContainer reader = await database.GetContainer("items", cancellationToken: cancellationToken);
        var windows = 0;
        proxy.AfterFields = async () =>
        {
            if (++windows == 1) await fixture.Database.Execute(new LibrarianBatch(Writes("after")), cancellationToken: cancellationToken);
        };
        var rows = await reader.GetAllItems(cancellationToken: cancellationToken);
        if (rows.Count != 260 || rows.Any(row => row != "after"))
            throw new Exception("Collection read mixed document versions across windows.");
    }
}
