using Soenneker.Utils.File.Abstract;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.FileSystem;
using Soenneker.Librarian.Redis;

namespace Soenneker.Librarian.Suite.Tests;

// Share the Redis integration constraint; competing operations within a test still run concurrently.
[NotInParallel("Redis")]
public class TransactionTests
{
    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async ValueTask Cross_container_conditions_and_writes_commit_together(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianDatabase db = fixture.Database;
        ILibrarianContainer jobs = await db.GetContainer("jobs", cancellationToken: cancellationToken);
        ILibrarianContainer leases = await db.GetContainer("leases", cancellationToken: cancellationToken);
        await jobs.AddItem("job", "queued", cancellationToken: cancellationToken);
        var claim = new LibrarianBatch(
            [new LibrarianWrite("jobs", "JOB", "running"), new LibrarianWrite("leases", "job", "token-1")],
            [new LibrarianCondition("jobs", "job", "queued"), new LibrarianCondition("leases", "job", null)]);
        Check(await db.Execute(claim, cancellationToken: cancellationToken), "Claim did not commit.");
        Check(await jobs.GetItem("job", cancellationToken: cancellationToken) == "running" && await leases.GetItem("job", cancellationToken: cancellationToken) == "token-1", "Claim was incomplete.");
        Check(!await db.Execute(claim, cancellationToken: cancellationToken), "Stale condition succeeded.");
        Check(await db.Execute(new LibrarianBatch([new LibrarianWrite("jobs", "job", "done"), new LibrarianWrite("leases", "JOB", null)], [new LibrarianCondition("leases", "job", "token-1")]), cancellationToken: cancellationToken), "Finish failed.");
        Check(await jobs.GetItem("job", cancellationToken: cancellationToken) == "done" && await leases.GetItem("job", cancellationToken: cancellationToken) is null, "Finish was incomplete.");
        await (await db.GetContainer("Jobs", cancellationToken: cancellationToken)).AddItem("job", "separate", cancellationToken: cancellationToken);
        Check(await jobs.GetItem("job", cancellationToken: cancellationToken) == "done", "Container case sensitivity changed.");
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async ValueTask Rejected_or_cancelled_batch_changes_nothing(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianDatabase db = fixture.Database;
        ILibrarianContainer items = await db.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("a", "original", cancellationToken: cancellationToken);
        Check(!await db.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", "changed"), new LibrarianWrite("other", "new", "new")], [new LibrarianCondition("items", "a", "stale")]), cancellationToken: cancellationToken), "Condition ignored.");
        try { await db.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", "cancelled")]), new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Check(await items.GetItem("a", cancellationToken: cancellationToken) == "original" && await (await db.GetContainer("other", cancellationToken: cancellationToken)).GetItem("new", cancellationToken: cancellationToken) is null, "Rejected batch leaked changes.");
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async ValueTask Index_validation_rolls_back_the_whole_batch_and_success_updates_indexes(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianDatabase db = fixture.Database;
        ILibrarianContainer items = await db.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("a", "{\"amount\":1}", cancellationToken: cancellationToken);
        await items.AddItem("b", "{\"amount\":1}", cancellationToken: cancellationToken);
        await items.EnsureIndex("amount", cancellationToken: cancellationToken);
        Check(items.BuildQueryable<RedisRow>().Count(row => row.Amount == 1) == 2, "Query setup failed.");
        try
        {
            await db.Execute(new LibrarianBatch([new LibrarianWrite("other", "x", "leaked"), new LibrarianWrite("items", "a", "{\"amount\":{}}") ]), cancellationToken: cancellationToken);
            throw new Exception("Invalid index value accepted.");
        }
        catch (ArgumentException) { }
        Check(await (await db.GetContainer("other", cancellationToken: cancellationToken)).GetItem("x", cancellationToken: cancellationToken) is null && await items.CountByIndex("amount", 1, cancellationToken: cancellationToken) == 2, "Validation leaked changes.");
        Check(await db.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", null), new LibrarianWrite("items", "b", "{\"amount\":2}"), new LibrarianWrite("items", "c", "{\"amount\":1}")]), cancellationToken: cancellationToken), "Indexed batch failed.");
        Check(await items.CountByIndex("amount", 1, cancellationToken: cancellationToken) == 1 && await items.CountByIndex("amount", 2, cancellationToken: cancellationToken) == 1, "Explicit indexes are stale.");
        Check(items.BuildQueryable<RedisRow>().Count(row => row.Amount == 1 || row.Amount == 2) == 2, "Automatic indexes are stale.");
        Check(await db.Execute(new LibrarianBatch([new LibrarianWrite("items", "c", null), new LibrarianWrite("items", "d", "{\"amount\":1}")]), cancellationToken: cancellationToken), "Bucket replacement failed.");
        Check(items.BuildQueryable<RedisRow>().Count(row => row.Amount == 1) == 1, "Same-value replacement lost the distinct index.");
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async ValueTask Only_one_competing_claim_commits(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianDatabase db = fixture.Database;
        await (await db.GetContainer("jobs", cancellationToken: cancellationToken)).AddItem("job", "queued", cancellationToken: cancellationToken);
        bool[] results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => db.Execute(new LibrarianBatch(
            [new LibrarianWrite("jobs", "job", "running"), new LibrarianWrite("leases", "job", i.ToString())], [new LibrarianCondition("jobs", "job", "queued")]), cancellationToken: cancellationToken).AsTask()));
        Check(results.Count(success => success) == 1, "More than one worker claimed the job.");
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async ValueTask Ordinary_reads_never_observe_half_a_batch(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianDatabase db = fixture.Database;
        ILibrarianContainer items = await db.GetContainer("items", cancellationToken: cancellationToken);
        await db.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", "0"), new LibrarianWrite("items", "b", "0")]), cancellationToken: cancellationToken);
        Task writer = Task.Run(async () =>
        {
            for (var i = 1; i <= 20; i++) await db.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", i.ToString()), new LibrarianWrite("items", "b", i.ToString())]), cancellationToken: cancellationToken);
        }, cancellationToken: cancellationToken);
        for (var i = 0; i < 30; i++)
        {
            List<string> values = await items.GetAllItems(cancellationToken: cancellationToken);
            Check(values.Count == 2 && values[0] == values[1], "Observed a partially published batch.");
        }
        await writer;
    }

    [Test]
    public async ValueTask Filesystem_failed_write_preserves_disk_and_memory_and_can_retry(CancellationToken cancellationToken)
    {
        await using var fixture = new PersistenceFixture();
        FileSystemLibrarianDatabase db = fixture.Database;
        ILibrarianContainer items = await db.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("a", "original", cancellationToken: cancellationToken);
        await db.Save(cancellationToken: cancellationToken);
        string before = (await fixture.Files.Inner.Read(fixture.Path, cancellationToken: cancellationToken));
        var batch = new LibrarianBatch([new LibrarianWrite("items", "a", "changed"), new LibrarianWrite("other", "b", "new")]);
        fixture.Files.BeforeWrite = (_, _) => throw new IOException("Injected batch failure");
        try { await db.Execute(batch, cancellationToken: cancellationToken); throw new Exception("Expected IO failure."); }
        catch (IOException) { }
        finally { fixture.Files.BeforeWrite = null; }
        Check((await fixture.Files.Inner.Read(fixture.Path, cancellationToken: cancellationToken)) == before && await items.GetItem("a", cancellationToken: cancellationToken) == "original", "Failed persistence leaked changes.");
        Check(await db.Execute(batch, cancellationToken: cancellationToken), "Retry failed.");
        Check((await fixture.Files.Inner.Read(fixture.Path, cancellationToken: cancellationToken)).Contains("changed") && (await fixture.Files.Inner.Read(fixture.Path, cancellationToken: cancellationToken)).Contains("new"), "Execute returned before persistence.");
    }

    [Test]
    public async ValueTask Redis_independent_instances_compete_on_the_same_condition(CancellationToken cancellationToken)
    {
        await using var fixture = new RedisPersistenceFixture();
        await using RedisLibrarianDatabase other = fixture.CreateDatabase();
        await (await fixture.Database.GetContainer("jobs", cancellationToken: cancellationToken)).AddItem("job", "queued", cancellationToken: cancellationToken);
        var first = new LibrarianBatch([new LibrarianWrite("jobs", "job", "running"), new LibrarianWrite("leases", "job", "first")], [new LibrarianCondition("jobs", "job", "queued")]);
        var second = new LibrarianBatch([new LibrarianWrite("jobs", "job", "running"), new LibrarianWrite("leases", "job", "second")], [new LibrarianCondition("jobs", "job", "queued")]);
        bool[] results = await Task.WhenAll(fixture.Database.Execute(first, cancellationToken: cancellationToken).AsTask(), other.Execute(second, cancellationToken: cancellationToken).AsTask());
        Check(results.Count(success => success) == 1, "Cross-instance claim was not atomic.");
        Check(await (await other.GetContainer("leases", cancellationToken: cancellationToken)).GetItem("job", cancellationToken: cancellationToken) == (results[0] ? "first" : "second"), "Lease did not match the winner.");
    }

    [Test]
    [Arguments(2)]
    [Arguments(8)]
    [Arguments(9)]
    [Arguments(32)]
    public void Batch_validation_preserves_address_comparison_at_every_size(int size)
    {
        LibrarianWrite[] writes = Enumerable.Range(0, size).Select(i => new LibrarianWrite("items", "id-" + i, "value")).ToArray();
        LibrarianCondition[] conditions = Enumerable.Range(0, size).Select(i => new LibrarianCondition("items", "id-" + i, null)).ToArray();
        _ = new LibrarianBatch(writes, conditions);
        writes[^1] = new LibrarianWrite("Items", "ID-0", "separate container");
        _ = new LibrarianBatch(writes, conditions);
        writes[^1] = new LibrarianWrite("items", "ID-0", "duplicate");
        try { _ = new LibrarianBatch(writes); throw new Exception("Duplicate write accepted."); }
        catch (ArgumentException) { }
        conditions[^1] = new LibrarianCondition("items", "ID-0", null);
        try { _ = new LibrarianBatch([], conditions); throw new Exception("Duplicate condition accepted."); }
        catch (ArgumentException) { }
    }

    [Test]
    public void Batch_copies_inputs_and_rejects_duplicate_document_addresses()
    {
        LibrarianWrite[] writes = [new("items", "id", "original")];
        var batch = new LibrarianBatch(writes);
        writes[0] = new LibrarianWrite("items", "id", "changed");
        Check(batch.Writes[0].Value == "original", "Batch retained mutable input.");
        try { _ = new LibrarianBatch([new LibrarianWrite("items", "id", "one"), new LibrarianWrite("items", "ID", "two")]); throw new Exception("Duplicate accepted."); }
        catch (ArgumentException) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
