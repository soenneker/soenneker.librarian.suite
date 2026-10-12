using System.Text.Json;
using Soenneker.Librarian.Turso;
using Soenneker.Librarian.SurrealDb;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;

string path = Path.Combine(Path.GetTempPath(), "librarian-aot-" + Guid.NewGuid().ToString("N") + ".db");
try
{
    await using var turso = new TursoLibrarianDatabase("Data Source=" + path);
    await Verify(turso);
}
finally { foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
string? surrealConnection = Environment.GetEnvironmentVariable("LIBRARIAN_TEST_SURREALDB");
if (!string.IsNullOrEmpty(surrealConnection))
{
    await using var surreal = new SurrealDbLibrarianDatabase(surrealConnection, "aot-" + Guid.NewGuid().ToString("N"));
    await Verify(surreal);
}
Console.WriteLine("Remote provider Native AOT smoke passed (SurrealDB requires LIBRARIAN_TEST_SURREALDB).");

static async Task Verify(ILibrarianDatabase database)
{
    var container = await database.GetContainer("smoke");
    try
    {
        await container.AddItem("a", "{\"n\":2}");
        var item = await container.GetItemWithVersion("a") ?? throw new Exception("Missing document.");
        if (await container.UpdateItemIfVersion("a", "{\"n\":3}", item.Version) is null) throw new Exception("Revision failed.");
        await container.EnsureIndex("n");
        if ((await container.FindByIndex<JsonElement>("n", 3)).Items.Count != 1) throw new Exception("Index failed.");
        if (!await database.Execute(new LibrarianBatch([new("smoke", "b", "{}")])) ) throw new Exception("Batch failed.");
    }
    finally { await container.DeleteAllItems(); }
}
