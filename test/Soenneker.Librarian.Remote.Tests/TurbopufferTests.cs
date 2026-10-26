using System.Net;
using System.Text.Json;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Turbopuffer;
using Turbopuffer;
using Turbopuffer.Exceptions;

namespace Soenneker.Librarian.Remote.Tests;

public sealed class TurbopufferTests
{
    [Test]
    [Arguments(0xD800)]
    [Arguments(0xDFFF)]
    public async Task Malformed_names_cannot_alias_valid_namespaces(int surrogate, CancellationToken token)
    {
        using var handler = new TurbopufferTestHandler();
        using var client = Client(handler);
        string invalid = "tenant-" + (char)surrogate;
        try
        {
            await using var rejected = new TurbopufferLibrarianDatabase(client, invalid);
            throw new Exception("Malformed database key was accepted.");
        }
        catch (System.Text.EncoderFallbackException) { }
        await using var database = new TurbopufferLibrarianDatabase(client, "tenant-\uFFFD");
        _ = await database.GetContainer("tenant-\uFFFD", token);
        try
        {
            _ = await database.GetContainer(invalid, token);
            throw new Exception("Malformed container name was accepted.");
        }
        catch (System.Text.EncoderFallbackException) { }
        Check(handler.Requests.Count == 0, "Malformed identities caused network I/O.");
    }

    private static TurbopufferClient Client(TurbopufferTestHandler handler) => new()
    {
        ApiKey = "test", BaseUrl = "https://turbopuffer.invalid", HttpClient = new HttpClient(handler), Compression = false,
        MaxRetries = 4
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [Test]
    public async Task Conditional_writes_preserve_create_update_and_version_semantics(CancellationToken token)
    {
        using var handler = new TurbopufferTestHandler { Respond = _ => "{\"rows_affected\":1}" };
        using var client = Client(handler);
        await using var database = new TurbopufferLibrarianDatabase(client);
        ILibrarianContainer container = await database.GetContainer("documents", token);
        string original = " {\"nested\": {\"value\":1.00}} ";
        await container.AddItem("A", original, token);
        JsonElement insert = handler.Requests[^1].Body;
        Check(insert.GetProperty("upsert_condition").GetRawText() == "[\"id\",\"Eq\",null]", "Create was not conditional.");
        Check(insert.GetProperty("upsert_rows")[0].GetProperty("librarian_document").GetString() == original, "JSON changed.");
        Check(!insert.GetProperty("schema").GetProperty("librarian_document").GetProperty("filterable").GetBoolean(), "JSON payload was indexed.");
        handler.Respond = _ => "{\"rows_affected\":0}";
        try { await container.AddItem("A", original, token); throw new Exception("Duplicate insert succeeded."); }
        catch (InvalidOperationException) { }
        Check(await container.UpdateItem("missing", "{}", token) is null, "Missing update created a document.");
        Check(handler.Requests[^1].Body.TryGetProperty("patch_rows", out _), "Update used upsert.");
        Check(await container.UpdateItemIfVersion("A", "{}", "old", token) is null, "Stale update succeeded.");
        Check(handler.Requests[^1].Body.GetProperty("patch_condition")[2].GetString() == "old", "Version condition missing.");
        handler.Respond = _ => "{\"rows_affected\":1}";
        LibrarianItem<string>? written = await container.UpdateItemIfVersion("A", "{}", "old", token);
        Check(written?.Version == handler.Requests[^1].Body.GetProperty("patch_rows")[0].GetProperty("librarian_revision").GetString(), "Returned revision differs from stored revision.");
        Check(await container.DeleteItemIfVersion("A", written!.Version, token), "Conditional delete failed.");
        Check(handler.Requests[^1].Body.GetProperty("delete_condition")[2].GetString() == written.Version, "Delete version condition missing.");
    }

    [Test]
    public async Task Bulk_reads_preserve_order_duplicates_missing_ids_and_case(CancellationToken token)
    {
        using var handler = new TurbopufferTestHandler
        {
            Respond = _ => "{\"rows\":[{\"id\":\"A\",\"librarian_document\":\"{}\",\"librarian_revision\":\"r1\"}]}"
        };
        using var client = Client(handler);
        await using var database = new TurbopufferLibrarianDatabase(client);
        ILibrarianContainer container = await database.GetContainer("documents", token);
        string?[] values = await container.GetItems(["a", "A", "missing", "A"], token);
        Check(values.SequenceEqual([null, "{}", null, "{}"]), "Bulk order or identity changed.");
        Check(handler.Requests.Count == 1, "Bulk read was split.");
        Check(handler.Requests[0].Body.GetProperty("consistency").GetProperty("level").GetString() == "strong", "Read was not strongly consistent.");
        Check((await container.GetItemWithVersion("A", token))?.Version == "r1", "Version read failed.");
        int count = handler.Requests.Count;
        Check((await container.GetItems([], token)).Length == 0 && handler.Requests.Count == count, "Empty read performed I/O.");
    }

    [Test]
    public async Task Enumeration_pages_and_count_uses_aggregation(CancellationToken token)
    {
        using var handler = new TurbopufferTestHandler();
        using var client = Client(handler);
        await using var database = new TurbopufferLibrarianDatabase(client);
        ILibrarianContainer container = await database.GetContainer("documents", token);
        handler.Respond = body => body.TryGetProperty("filters", out _)
            ? "{\"rows\":[{\"id\":\"1000\"}]}"
            : "{\"rows\":[" + string.Join(',', Enumerable.Range(0, 1000).Select(i => "{\"id\":\"" + i.ToString("D4") + "\"}")) + "]}";
        List<string> ids = await container.GetAllIds(token);
        Check(ids.Count == 1001 && handler.Requests.Count == 2, "Enumeration was truncated.");
        Check(handler.Requests[1].Body.GetProperty("filters")[2].GetString() == "0999", "Paging cursor was incorrect.");
        Check(handler.Requests[0].Body.GetProperty("include_attributes").GetArrayLength() == 0, "ID listing fetched payloads.");
        handler.Respond = _ => "{\"aggregations\":{\"count\":1001}}";
        Check(await container.CountItems(token) == 1001, "Count failed.");
        Check(handler.Requests[^1].Body.TryGetProperty("aggregate_by", out _), "Count fetched documents.");
    }

    [Test]
    public async Task Namespace_isolation_unload_and_caller_owned_client(CancellationToken token)
    {
        using var handler = new TurbopufferTestHandler();
        using var client = Client(handler);
        var database = new TurbopufferLibrarianDatabase(client, "ab");
        await using var other = new TurbopufferLibrarianDatabase(client, "a");
        ILibrarianContainer first = await database.GetContainer("c", token);
        await first.GetItem("x", token);
        await (await other.GetContainer("bc", token)).GetItem("x", token);
        await (await database.GetContainer("C", token)).GetItem("x", token);
        Check(handler.Requests.Select(x => x.Path).Distinct().Count() == 3, "Namespace collision.");
        await database.UnloadContainer("c", token);
        try { await first.GetItem("x", token); throw new Exception("Unloaded handle accepted."); }
        catch (ObjectDisposedException) { }
        await (await database.GetContainer("c", token)).GetItem("x", token);
        Check(handler.Requests[0].Path == handler.Requests[^1].Path, "Namespace changed after unload.");
        await database.DisposeAsync();
        Check(!handler.Disposed, "Caller-owned client was disposed.");
    }

    [Test]
    public async Task Missing_namespace_is_empty_but_server_failures_are_not_retried(CancellationToken token)
    {
        using var handler = new TurbopufferTestHandler { Status = HttpStatusCode.NotFound, Respond = _ => "{\"error\":\"not found\"}" };
        using var client = Client(handler);
        await using var database = new TurbopufferLibrarianDatabase(client);
        ILibrarianContainer container = await database.GetContainer("documents", token);
        Check(await container.GetItem("x", token) is null, "Missing namespace was not empty.");
        Check(await container.CountItems(token) == 0, "Missing namespace count was not zero.");
        Check(await container.UpdateItem("x", "{}", token) is null, "Missing namespace update succeeded.");
        handler.Status = HttpStatusCode.InternalServerError;
        int before = handler.Requests.Count;
        try { await container.AddItem("x", "{}", token); throw new Exception("Server failure was hidden."); }
        catch (Turbopuffer5xxException) { }
        Check(handler.Requests.Count == before + 1, "Uncertain write was retried.");
    }

    [Test]
    public async Task Validation_cancellation_and_unsupported_operations_do_not_send_requests(CancellationToken token)
    {
        using var handler = new TurbopufferTestHandler();
        using var client = Client(handler);
        await using var database = new TurbopufferLibrarianDatabase(client);
        ILibrarianContainer container = await database.GetContainer("documents", token);
        try { await container.AddItem(new string('é', 33), "{}", token); throw new Exception("Oversized ID accepted."); }
        catch (ArgumentException) { }
        try { await container.AddItem("x", "invalid", token); throw new Exception("Invalid JSON accepted."); }
        catch (JsonException) { }
        try { await container.GetItem("x", new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        try { await container.GetItems(Enumerable.Range(0, 10001).Select(i => i.ToString()).ToArray(), token); throw new Exception("Oversized bulk read accepted."); }
        catch (ArgumentException) { }
        try { container.BuildQueryable<JsonElement>(); throw new Exception("Unsupported LINQ accepted."); }
        catch (NotSupportedException) { }
        try { await ((ILibrarianDatabase)database).Execute(new LibrarianBatch([new("documents", "x", "{}")]), token); throw new Exception("Unsupported batch accepted."); }
        catch (NotSupportedException) { }
        await database.Save(token);
        Check(handler.Requests.Count == 0, "Validation performed I/O.");
    }

    [Test]
    public async Task Clear_continues_partial_deletes_without_deleting_namespace(CancellationToken token)
    {
        using var handler = new TurbopufferTestHandler();
        handler.Respond = _ => handler.Requests.Count == 1 ? "{\"rows_affected\":10,\"rows_remaining\":true}" : "{\"rows_affected\":1,\"rows_remaining\":false}";
        using var client = Client(handler);
        await using var database = new TurbopufferLibrarianDatabase(client);
        await (await database.GetContainer("documents", token)).DeleteAllItems(token);
        Check(handler.Requests.Count == 2 && handler.Requests.All(r => r.Body.GetProperty("delete_by_filter_allow_partial").GetBoolean()), "Partial clear did not continue.");
    }
}
