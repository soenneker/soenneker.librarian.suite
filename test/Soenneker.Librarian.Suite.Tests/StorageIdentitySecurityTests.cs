using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Cloudflare.R2;
using Soenneker.Librarian.R2;
using Soenneker.Librarian.CouchDb;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class StorageIdentitySecurityTests
{
    [Test]
    [Arguments(0xD800)]
    [Arguments(0xDFFF)]
    public async Task R2_rejects_aliases_without_changing_valid_storage(int surrogate, CancellationToken token)
    {
        string invalid = "tenant-" + (char)surrogate;
        const string valid = "tenant-\uFFFD";
        using var handler = new R2ObjectHttpHandler();
        using var http = new HttpClient(handler);
        var store = new CloudflareR2WorkerObjectStore(http, new Uri("https://r2.example/"), "test-token");
        await using var db = new R2ObjectLibrarianDatabase(store, valid);
        var container = await db.GetContainer(valid, token);
        await container.AddItem(valid, "private", token);
        await Reject(async () => { await using var rejected = new R2ObjectLibrarianDatabase(store, invalid); });
        await Reject(async () => { _ = await db.GetContainer(invalid, token); });
        await Reject(async () => { _ = await container.GetItem(invalid, token); });
        await Reject(async () => { _ = await container.UpdateItemIfVersion(invalid, "overwrite", "etag", token); });
        await Reject(async () => { _ = await container.DeleteItemIfVersion(invalid, "etag", token); });
        if (handler.Writes != 1 || await container.GetItem(valid, token) != "private")
            throw new Exception("Malformed identity changed valid storage.");
    }

    [Test]
    [Arguments(0xD800)]
    [Arguments(0xDFFF)]
    public async Task Couchdb_rejects_malformed_names_partitions_and_ids_before_io(int surrogate, CancellationToken token)
    {
        string invalid = "tenant-" + (char)surrogate;
        using var handler = new CouchDbTestHandler();
        using var http = new HttpClient(handler);
        await Reject(async () => { await using var rejected = new CouchDbLibrarianDatabase(
            new CouchDbLibrarianOptions { Endpoint = new Uri("https://couch.example/"), Key = invalid }, http); });
        await using var db = new CouchDbLibrarianDatabase(new CouchDbLibrarianOptions
            { Endpoint = new Uri("https://couch.example/"), Key = "test" }, http);
        await Reject(async () => { _ = await db.GetContainer(invalid, token); });
        await Reject(async () => { _ = await db.GetContainer("items", invalid, token); });
        if (handler.Calls != 0) throw new Exception("Invalid namespace caused I/O.");
        handler.Expect(HttpMethod.Head, "/librarian-74657374-6974656d73");
        var container = await db.GetContainer("items", token);
        await Reject(async () => { _ = await container.GetItem(invalid, token); });
        await Reject(async () => { _ = await container.GetItem("partition:" + invalid, token); });
        await Reject(async () => { _ = await container.DeleteItemIfVersion(invalid, "rev", token); });
        if (handler.Calls != 1) throw new Exception("Invalid document identity caused I/O.");
        handler.Verify();
    }

    private static async Task Reject(Func<Task> operation)
    {
        try { await operation(); }
        catch (EncoderFallbackException) { return; }
        throw new Exception("Malformed Unicode storage identity was accepted.");
    }
}
