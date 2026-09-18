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
    public async Task Cross_container_conditions_and_writes_commit_together(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianDatabase db = fixture.Database;
        ILibrarianContainer jobs = await db.GetContainer("jobs");
        ILibrarianContainer leases = await db.GetContainer("leases");
        await jobs.AddItem("job", "queued");
        var claim = new LibrarianBatch(
            [new("jobs", "JOB", "running"), new("leases", "job", "token-1")],
            [new("jobs", "job", "queued"), new("leases", "job", null)]);
        Check(await db.Execute(claim), "Claim did not commit.");
        Check(await jobs.GetItem("job") == "running" && await leases.GetItem("job") == "token-1", "Claim was incomplete.");
        Check(!await db.Execute(claim), "Stale condition succeeded.");
        Check(await db.Execute(new([new("jobs", "job", "done"), new("leases", "JOB", null)], [new("leases", "job", "token-1")])), "Finish failed.");
        Check(await jobs.GetItem("job") == "done" && await leases.GetItem("job") is null, "Finish was incomplete.");
        await (await db.GetContainer("Jobs")).AddItem("job", "separate");
        Check(await jobs.GetItem("job") == "done", "Container case sensitivity changed.");
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    public async Task Rejected_or_cancelled_batch_changes_nothing(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianDatabase db = fixture.Database;
        ILibrarianContainer items = await db.GetContainer("items");
        await items.AddItem("a", "original");
        Check(!await db.Execute(new([new("items", "a", "changed"), new("other", "new", "new")], [new("items", "a", "stale")])), "Condition ignored.");
        try { await db.Execute(new([new("items", "a", "cancelled")]), new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Check(await items.GetItem("a") == "original" && await (await db.GetContainer("other")).GetItem("new") is null, "Rejected batch leaked changes.");
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    public async Task Index_validation_rolls_back_the_whole_batch_and_success_updates_indexes(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianDatabase db = fixture.Database;
        ILibrarianContainer items = await db.GetContainer("items");
        await items.AddItem("a", "{\"amount\":1}");
        await items.AddItem("b", "{\"amount\":1}");
        await items.EnsureIndex("amount");
        Check(items.BuildQueryable<RedisRow>().Count(row => row.Amount == 1) == 2, "Query setup failed.");
        try
        {
            await db.Execute(new([new("other", "x", "leaked"), new("items", "a", "{\"amount\":{}}") ]));
            throw new Exception("Invalid index value accepted.");
        }
        catch (ArgumentException) { }
        Check(await (await db.GetContainer("other")).GetItem("x") is null && await items.CountByIndex("amount", 1) == 2, "Validation leaked changes.");
        Check(await db.Execute(new([new("items", "a", null), new("items", "b", "{\"amount\":2}"), new("items", "c", "{\"amount\":1}")])), "Indexed batch failed.");
        Check(await items.CountByIndex("amount", 1) == 1 && await items.CountByIndex("amount", 2) == 1, "Explicit indexes are stale.");
        Check(items.BuildQueryable<RedisRow>().Count(row => row.Amount == 1 || row.Amount == 2) == 2, "Automatic indexes are stale.");
        Check(await db.Execute(new([new("items", "c", null), new("items", "d", "{\"amount\":1}")])), "Bucket replacement failed.");
        Check(items.BuildQueryable<RedisRow>().Count(row => row.Amount == 1) == 1, "Same-value replacement lost the distinct index.");
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    public async Task Only_one_competing_claim_commits(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianDatabase db = fixture.Database;
        await (await db.GetContainer("jobs")).AddItem("job", "queued");
        bool[] results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => db.Execute(new(
            [new("jobs", "job", "running"), new("leases", "job", i.ToString())], [new("jobs", "job", "queued")])).AsTask()));
        Check(results.Count(success => success) == 1, "More than one worker claimed the job.");
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    public async Task Ordinary_reads_never_observe_half_a_batch(string provider)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianDatabase db = fixture.Database;
        ILibrarianContainer items = await db.GetContainer("items");
        await db.Execute(new([new("items", "a", "0"), new("items", "b", "0")]));
        Task writer = Task.Run(async () =>
        {
            for (int i = 1; i <= 20; i++) await db.Execute(new([new("items", "a", i.ToString()), new("items", "b", i.ToString())]));
        });
        for (int i = 0; i < 30; i++)
        {
            List<string> values = await items.GetAllItems();
            Check(values.Count == 2 && values[0] == values[1], "Observed a partially published batch.");
        }
        await writer;
    }

    [Test]
    public async Task Filesystem_failed_write_preserves_disk_and_memory_and_can_retry()
    {
        await using var fixture = new PersistenceFixture();
        FileSystemLibrarianDatabase db = fixture.Database;
        ILibrarianContainer items = await db.GetContainer("items");
        await items.AddItem("a", "original");
        await db.Save();
        string before = File.ReadAllText(fixture.Path);
        var batch = new LibrarianBatch([new("items", "a", "changed"), new("other", "b", "new")]);
        fixture.Files.BeforeWrite = (_, _) => throw new IOException("Injected batch failure");
        try { await db.Execute(batch); throw new Exception("Expected IO failure."); }
        catch (IOException) { }
        finally { fixture.Files.BeforeWrite = null; }
        Check(File.ReadAllText(fixture.Path) == before && await items.GetItem("a") == "original", "Failed persistence leaked changes.");
        Check(await db.Execute(batch), "Retry failed.");
        Check(File.ReadAllText(fixture.Path).Contains("changed") && File.ReadAllText(fixture.Path).Contains("new"), "Execute returned before persistence.");
    }

    [Test]
    public async Task Redis_independent_instances_compete_on_the_same_condition()
    {
        await using var fixture = new RedisPersistenceFixture();
        await using RedisLibrarianDatabase other = fixture.CreateDatabase();
        await (await fixture.Database.GetContainer("jobs")).AddItem("job", "queued");
        var first = new LibrarianBatch([new("jobs", "job", "running"), new("leases", "job", "first")], [new("jobs", "job", "queued")]);
        var second = new LibrarianBatch([new("jobs", "job", "running"), new("leases", "job", "second")], [new("jobs", "job", "queued")]);
        bool[] results = await Task.WhenAll(fixture.Database.Execute(first).AsTask(), other.Execute(second).AsTask());
        Check(results.Count(success => success) == 1, "Cross-instance claim was not atomic.");
        Check(await (await other.GetContainer("leases")).GetItem("job") == (results[0] ? "first" : "second"), "Lease did not match the winner.");
    }

    [Test]
    public void Batch_copies_inputs_and_rejects_duplicate_document_addresses()
    {
        LibrarianWrite[] writes = [new("items", "id", "original")];
        var batch = new LibrarianBatch(writes);
        writes[0] = new("items", "id", "changed");
        Check(batch.Writes[0].Value == "original", "Batch retained mutable input.");
        try { _ = new LibrarianBatch([new("items", "id", "one"), new("items", "ID", "two")]); throw new Exception("Duplicate accepted."); }
        catch (ArgumentException) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
