using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

internal static class VersionedProviderContract
{
    internal static async Task Exercise(ILibrarianContainer first, ILibrarianContainer second)
    {
        Check(await first.GetItemWithVersion("missing") is null, "Missing versioned read failed.");
        await first.AddItem("counter", NativeDocumentJson.Create("counter", "{\"score_value\":0}", "org-a"));
        LibrarianItem<string> original = (await first.GetItemWithVersion("counter"))!;
        LibrarianItem<string>?[] outcomes = await Task.WhenAll(first.UpdateItemIfVersion("counter", NativeDocumentJson.Create("counter", "{\"score_value\":1}", "org-a"), original.Version).AsTask(),
            second.UpdateItemIfVersion("counter", NativeDocumentJson.Create("counter", "{\"score_value\":2}", "org-a"), original.Version).AsTask());
        Check((outcomes[0] is null) != (outcomes[1] is null), "Competing versions both won or both lost.");
        Check(!await first.DeleteItemIfVersion("counter", original.Version), "Stale delete succeeded.");
        await first.UpdateItem("counter", original.Document);
        Check(await first.UpdateItemIfVersion("counter", original.Document, original.Version) is null, "ABA update accepted an old version.");
        await Task.WhenAll(Increment(first), Increment(second));
        Check((await first.GetItemWithVersion<NativeDocument>("counter"))!.Document.Score == 6, "Concurrent mutations lost increments.");

        var callbacks = 0;
        try
        {
            await first.MutateItem<NativeDocument>("counter", item =>
            {
                callbacks++;
                second.UpdateItem("counter", NativeDocumentJson.Create("counter", "{\"score_value\":10}", "org-a")).AsTask().GetAwaiter().GetResult();
                item.Score++;
                return item;
            }, maxAttempts: 2);
            throw new Exception("Conflict exhaustion did not throw.");
        }
        catch (LibrarianConcurrencyException) { }
        Check(callbacks == 2, "Mutation retry limit changed.");
        try { await first.MutateItem<NativeDocument>("missing", item => item); throw new Exception("Missing mutation accepted."); }
        catch (KeyNotFoundException) { }
        try { await first.GetItemWithVersion("counter", new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        LibrarianItem<string> current = (await first.GetItemWithVersion("counter"))!;
        Check(await first.DeleteItemIfVersion("counter", current.Version), "Current version delete failed.");
        await first.AddItem("counter", current.Document);
        Check(!await first.DeleteItemIfVersion("counter", current.Version), "Delete/recreate reused a version.");
    }

    private static async Task Increment(ILibrarianContainer container)
    {
        for (var i = 0; i < 3; i++)
            await container.MutateItem<NativeDocument>("counter", item => { item.Score++; return item; }, maxAttempts: 16);
    }
}
