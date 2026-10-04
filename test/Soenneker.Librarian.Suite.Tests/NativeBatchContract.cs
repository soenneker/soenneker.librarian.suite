using System;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

internal static class NativeBatchContract
{
    internal static async Task Exercise(ILibrarianDatabase database)
    {
        ILibrarianContainer items = await database.GetContainer("batch", "org-a");
        string Json(string id, int score = 0) => NativeDocumentJson.Create(id, $"{{\"score_value\":{score}}}", "org-a");
        Check(await database.Execute(new LibrarianBatch([new LibrarianWrite("batch", "a", Json("a"), CreateOnly: true)]), "org-a"), "Create failed.");
        LibrarianItem<string> initial = (await items.GetItemWithVersion("a"))!;
        Check(await database.Execute(new LibrarianBatch([new LibrarianWrite("batch", "a", Json("a", 1), initial.Version),
            new LibrarianWrite("batch", "audit", Json("audit"), CreateOnly: true)]), "org-a"), "Versioned batch failed.");
        Check(await items.GetItem("audit") is not null, "Batch insertion missing.");
        Check(!await database.Execute(new LibrarianBatch([new LibrarianWrite("batch", "rollback", Json("rollback"), CreateOnly: true),
            new LibrarianWrite("batch", "a", Json("a", 2), initial.Version)]), "org-a"), "Stale version accepted.");
        Check(await items.GetItem("rollback") is null && NativeDocumentJson.Score((await items.GetItem("a"))!) == 1, "Conflict did not roll back all writes.");
        LibrarianItem<string> current = (await items.GetItemWithVersion("a"))!;
        Check(!await database.Execute(new LibrarianBatch([new LibrarianWrite("batch", "a", Json("a", 2), current.Version),
            new LibrarianWrite("batch", "audit", Json("audit"), CreateOnly: true)]), "org-a"), "Duplicate creation accepted.");
        Check((await items.GetItemWithVersion("a"))!.Version == current.Version, "Failed create changed another document's version.");
        Check(!await database.Execute(new LibrarianBatch([new LibrarianWrite("batch", "a", null, initial.Version)]), "org-a"), "Stale deletion succeeded.");
        Check(await database.Execute(new LibrarianBatch([new LibrarianWrite("batch", "a", null, current.Version)]), "org-a"), "Versioned deletion failed.");
        Check(!await database.Execute(new LibrarianBatch([new LibrarianWrite("batch", "a", null)]), "org-a"), "Missing native batch delete reported success.");
        Check(!await database.Execute(new LibrarianBatch([new LibrarianWrite("batch", "a", Json("a"), current.Version)]), "org-a"), "Versioned replacement created a missing document.");
        try { await database.Execute(new LibrarianBatch([], [new LibrarianCondition("batch", "a", null)]), "org-a"); throw new Exception("Raw condition accepted."); }
        catch (NotSupportedException) { }
        try { await database.Execute(new LibrarianBatch([new LibrarianWrite("batch", "a", Json("a")), new LibrarianWrite("batch", "org-a:a", Json("a"))]), "org-a"); throw new Exception("Aliased duplicate identity accepted."); }
        catch (ArgumentException) { }
    }
}
