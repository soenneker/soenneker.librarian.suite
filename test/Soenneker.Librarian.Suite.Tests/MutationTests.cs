using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Librarian.Core;

namespace Soenneker.Librarian.Suite.Tests;

public class MutationTests
{

    [Test]
    public async Task Reads_honor_cancellation_and_strict_missing_behavior()
    {
        using var container = new LibrarianContainer("items", new MutationDatabase(), NullLogger.Instance);
        await container.AddItem("one", "original");
        var token = new CancellationToken(true);
        Func<ValueTask>[] operations =
        [
            async () => { await container.GetItem("one", token); },
            async () => { await container.GetItemStrict("one", token); },
            async () => { await container.GetAllItems(token); },
            async () => { await container.GetAllIds(token); },
            async () => { await container.GetLibrarianItems(token); }
        ];
        foreach (Func<ValueTask> operation in operations)
        {
            try { await operation(); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
        }
        if (await container.GetItem("missing") is not null || await container.GetItemStrict("ONE") != "original")
            throw new Exception("Read lookup semantics changed.");
        try { await container.GetItemStrict("missing"); throw new Exception("Expected missing item failure."); }
        catch (KeyNotFoundException) { }
        List<IdValuePair> snapshot = await container.GetLibrarianItems();
        snapshot.Clear();
        if (await container.GetItem("one") != "original")
            throw new Exception("Snapshot modified the container.");
    }

    [Test]
    public async Task Cancelled_mutations_leave_data_unchanged()
    {
        var database = new MutationDatabase();
        using var container = new LibrarianContainer("items", database, NullLogger.Instance);
        await container.AddItem("one", "original");
        var token = new CancellationToken(true);
        Func<ValueTask>[] operations =
        [
            async () => { await container.AddItem("two", "new", token); },
            async () => { await container.UpdateItem("one", "changed", token); },
            async () => { await container.UpdateItemStrict("one", "changed", token); },
            () => container.DeleteItem("one", token),
            () => container.DeleteAllItems(token)
        ];
        foreach (Func<ValueTask> operation in operations)
        {
            try { await operation(); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
        }
        if ((await container.GetItem("one")) != "original" || (await container.GetItem("two")) != null || database.DirtyCount != 1)
            throw new Exception("Cancelled operation mutated data.");
    }

    [Test]
    public async Task Identical_updates_skip_dirty_tracking_and_committed_changes_use_uncancellable_token()
    {
        var database = new MutationDatabase();
        using var container = new LibrarianContainer("items", database, NullLogger.Instance);
        using var cts = new CancellationTokenSource();
        await container.AddItem("one", "original", cts.Token);
        await container.UpdateItem("ONE", "original", cts.Token);
        await container.UpdateItemStrict("one", "original", cts.Token);
        if (database.DirtyCount != 1 || database.LastToken.CanBeCanceled)
            throw new Exception("No-op update or dirty tracking token is incorrect.");
        await container.UpdateItem("one", "changed", cts.Token);
        if (database.DirtyCount != 2 || (await container.GetItem("one")) != "changed")
            throw new Exception("Changed update was not tracked.");
    }

    [Test]
    public async Task Bulk_reads_return_detached_lists_with_all_entries()
    {
        using var container = new LibrarianContainer("items", new MutationDatabase(), NullLogger.Instance);
        await container.AddItem("one", "first");
        await container.AddItem("two", "second");
        List<string> ids = (await container.GetAllIds());
        List<string> items = (await container.GetAllItems());
        if (ids.Count != 2 || !ids.Contains("one") || !ids.Contains("two") || !items.Contains("first") || !items.Contains("second"))
            throw new Exception("Bulk read omitted entries.");
        ids.Clear();
        items.Clear();
        if ((await container.GetItem("one")) != "first") throw new Exception("Returned lists modified the container.");
    }
}
