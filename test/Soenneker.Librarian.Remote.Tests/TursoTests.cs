using System.Text.Json;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Turso;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Remote.Tests;

public sealed class TursoTests
{
    [Test]
    public async Task Transactions_indexes_persistence_and_versions(CancellationToken token)
    {
        string path = Path.Combine(Path.GetTempPath(), "librarian-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var first = new TursoLibrarianDatabase("Data Source=" + path);
            await using var second = new TursoLibrarianDatabase("Data Source=" + path);
            ILibrarianContainer a = await first.GetContainer("a'quoted", token);
            await a.AddItem("A", " {\"n\":1.00} ", token);
            await a.EnsureIndex("n", token);
            ILibrarianContainer b = await second.GetContainer("a'quoted", token);
            Check(await b.GetItem("a", token) == " {\"n\":1.00} ", "Exact JSON or ID identity changed.");
            Check(await b.CountByIndex("n", 1, token) == 1, "Index was not persisted.");
            var version = await a.GetItemWithVersion("A", token);
            Check(await b.UpdateItemIfVersion("A", "{\"n\":2}", version!.Version, token) is not null, "Versioned write failed.");
            Check(await a.UpdateItemIfVersion("A", "{\"n\":3}", version.Version, token) is null, "Stale revision was accepted.");
            Check(await a.CountByIndex("n", 1, token) == 0 && await a.CountByIndex("n", 2, token) == 1, "Index update was not atomic.");
            Check(!await first.Execute(new LibrarianBatch([new("a'quoted", "x", "{\"n\":4}")], [new("a'quoted", "A", "wrong")]), token), "Failed condition committed.");
            Check(await a.GetItem("x", token) is null, "Failed batch wrote a document.");
            try
            {
                await first.Execute(new LibrarianBatch([new("a'quoted", "x", "{\"n\":4}"), new("a'quoted", "y", "invalid-json")]), token);
                throw new Exception("Invalid indexed document committed.");
            }
            catch (JsonException) { }
            Check(await a.GetItem("x", token) is null, "Validation failure partially committed.");
            await a.AddItem("b", "{\"n\":-2}", token);
            await a.AddItem("c", "{\"n\":2.5}", token);
            var page = await a.FindRangeByIndex<JsonElement>("n", -2, 3, skip: 1, take: 2, cancellationToken: token);
            Check(page.Items.Select(x => x.GetProperty("n").GetDecimal()).SequenceEqual([2m, 2.5m]), "Range order or paging failed.");
            Check(await a.BuildQueryable<JsonElement>().Take(1).Skip(9).CountAsync(token) == 0, "Paging count failed.");
            LibrarianJson.Register(RemoteJsonContext.Default.RemoteRow);
            GeneratedQueryMetadata.Register<RemoteRow, int>(nameof(RemoteRow.Amount), "Amount", row => row.Amount, true);
            var queryContainer = await first.GetContainer("query", token);
            for (int i = 0; i < 5; i++) await queryContainer.AddItem(i.ToString(), "{\"Amount\":" + i + "}", token);
            var selected = await queryContainer.BuildQueryable<RemoteRow>().OrderByDescending(row => row.Amount).Skip(1).Take(2).ToListAsync(token);
            Check(selected.Select(row => row.Amount).SequenceEqual([3, 2]), "LINQ ordering and paging failed.");
            Check(await queryContainer.BuildQueryable<RemoteRow>().Where(row => row.Amount == 2).AnyAsync(token), "LINQ predicate failed.");
            var revision = await queryContainer.GetItemWithVersion("2", token);
            Check(!await first.Execute(new LibrarianBatch([new("query", "new", "{}", CreateOnly: true), new("query", "2", "{}", ExpectedVersion: "stale")]), token), "Versioned batch accepted a stale revision.");
            Check(await queryContainer.GetItem("new", token) is null, "Versioned batch partially committed.");
            Check(await first.Execute(new LibrarianBatch([new("query", "2", "{\"Amount\":9}", ExpectedVersion: revision!.Version)]), token), "Versioned batch did not commit.");
            await first.UnloadContainer("a'quoted", token);
            try { await a.GetItem("A", token); throw new Exception("Unloaded handle accepted."); } catch (ObjectDisposedException) { }
            await b.DeleteAllItems(token);
            Check(await b.CountByIndex("n", 2, token) == 0, "Clear left stale index values.");
            await b.AddItem("a", "{\"n\":9}", token);
            await b.DeleteItem("a", token);
            Check(await b.CountByIndex("n", 9, token) == 0, "Delete left stale index values.");
        }
        finally
        {
            foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    [Test]
    public async Task Conditional_writers_do_not_both_win(CancellationToken token)
    {
        string path = Path.Combine(Path.GetTempPath(), "librarian-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await using var first = new TursoLibrarianDatabase("Data Source=" + path);
            await using var second = new TursoLibrarianDatabase("Data Source=" + path);
            var a = await first.GetContainer("items", token);
            await a.AddItem("guard", "0", token);
            var b = await second.GetContainer("items", token);
            await b.GetItem("guard", token);
            async Task<bool> Attempt(TursoLibrarianDatabase db, string value)
            {
                try { return await db.Execute(new LibrarianBatch([new("items", "guard", value)], [new("items", "guard", "0")]), token); }
                catch (System.Data.Common.DbException) { return false; }
            }
            bool[] results = await Task.WhenAll(Attempt(first, "1"), Attempt(second, "2"));
            Check(results.Count(x => x) == 1, "Conditional writers both won or both failed.");
        }
        finally { foreach (string suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
