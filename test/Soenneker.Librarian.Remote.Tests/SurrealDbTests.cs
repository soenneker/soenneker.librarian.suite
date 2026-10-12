using System.Text.Json;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.SurrealDb;
using SurrealDb.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Soenneker.Librarian.Remote.Tests;

public sealed class SurrealDbTests
{
    [Test]
    public async Task Native_transactions_revisions_and_indexes(CancellationToken token)
    {
        string? connection = Environment.GetEnvironmentVariable("LIBRARIAN_TEST_SURREALDB");
        if (string.IsNullOrEmpty(connection)) Skip.Test("Set LIBRARIAN_TEST_SURREALDB to a SurrealDB 3.3+ test connection string.");
        string key = "test" + Guid.NewGuid().ToString("N");
        await using var client = new SurrealDbClient(SurrealDbOptions.Create().FromConnectionString(connection!).Build());
        await using var database = new SurrealDbLibrarianDatabase(client, key);
        await using var other = new SurrealDbLibrarianDatabase(connection!, key);
        string Hex(string text) => string.Concat(text.Select(c => ((int)c).ToString("X4")));
        try
        {
            var a = await database.GetContainer("items", token);
            var b = await other.GetContainer("items", token);
            await a.AddItem("id", " {\"n\":1.00} ", token);
            var version = await b.GetItemWithVersion("id", token);
            Check(version?.Document == " {\"n\":1.00} ", "JSON did not round trip.");
            Check(await b.UpdateItemIfVersion("id", "{\"n\":2}", version!.Version, token) is not null, "Version update failed.");
            Check(await a.UpdateItemIfVersion("id", "{\"n\":3}", version.Version, token) is null, "Stale version accepted.");
            Check(!await database.Execute(new LibrarianBatch([new("items", "x", "{\"n\":5}", CreateOnly: true), new("items", "id", "{}", ExpectedVersion: version.Version)]), token), "Conflicting batch committed.");
            Check(await a.GetItem("x", token) is null, "Conflicting batch partially committed.");
            await a.EnsureIndex("n", token);
            Check(await b.CountByIndex("n", 2, token) == 1, "Index count failed.");
            Check((await b.FindByIndex<JsonElement>("n", 2, cancellationToken: token)).Items.Count == 1, "Index page failed.");
            await a.AddItem("other", "{\"n\":-1}", token);
            Check((await a.FindRangeByIndex<JsonElement>("n", -2, 4, cancellationToken: token)).Items.Select(x => x.GetProperty("n").GetInt32()).SequenceEqual([-1, 2]), "Range order failed.");
            await b.DeleteAllItems(token);
            Check(await a.CountByIndex("n", 2, token) == 0, "Clear left an indexed record.");
        }
        finally
        {
            (await client.RawQuery($"REMOVE TABLE IF EXISTS librarian_{Hex(key)}_{Hex("items")}; DELETE type::record('librarian_guards', $key);", new Dictionary<string, object?> { ["key"] = Hex(key) })).EnsureAllOks();
        }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
