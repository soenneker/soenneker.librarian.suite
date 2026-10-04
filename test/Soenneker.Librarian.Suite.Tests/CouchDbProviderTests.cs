using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.CouchDb;
using Soenneker.Librarian.CouchDb.Registrars;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CouchDbProviderTests
{
    private const string Db = "/librarian-74657374-6974656d73";
    private const string Id = "/d-6f7267-61";
    private const string Document = "{\"id\":\"a\",\"partitionKey\":\"org\",\"score_value\":1}";
    private const string Stored = "{\"_id\":\"d-6f7267-61\",\"_rev\":\"1-a\",\"id\":\"a\",\"partitionKey\":\"org\",\"score_value\":1}";
    private static CouchDbLibrarianOptions Options(bool ensure = true) => new()
    {
        Endpoint = new Uri("https://couch.invalid/proxy/"), Key = "test", EnsureDatabaseOnFirstUse = ensure,
        Username = "user", Password = "secret"
    };

    [Test]
    public async Task Initialization_is_lazy_serialized_and_handles_creation_races()
    {
        using var handler = new CouchDbTestHandler();
        using var client = new HttpClient(handler);
        await using var database = new CouchDbLibrarianDatabase(Options(), client);
        Check(handler.Calls == 0, "Construction performed I/O.");
        handler.Expect(HttpMethod.Head, Db, status: HttpStatusCode.NotFound);
        handler.Expect(HttpMethod.Put, Db, status: HttpStatusCode.PreconditionFailed, inspect: (request, _) =>
        {
            Check(request.RequestUri!.AbsolutePath.StartsWith("/proxy/", StringComparison.Ordinal), "Proxy prefix lost.");
            Check(request.Headers.Authorization?.Scheme == "Basic", "Authentication missing.");
        });
        handler.Expect(HttpMethod.Head, Db);
        ILibrarianContainer[] containers = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => database.GetContainer("items", "org").AsTask()));
        Check(containers.All(c => ReferenceEquals(c, containers[0])) && handler.Calls == 3, "Initialization was repeated.");
        await database.GetContainer("items", "other");
        Check(handler.Calls == 3, "A partition provisioned another database.");
        await database.UnloadContainer("items");
        try { await containers[0].GetItem("a"); throw new Exception("Unloaded container survived."); } catch (ObjectDisposedException) { }
        handler.Expect(HttpMethod.Head, Db);
        await database.GetContainer("items");
        await database.DisposeAsync();
        await database.DisposeAsync();
        Check(!handler.IsDisposed, "Caller-owned HTTP client was disposed.");
        try { await database.GetContainer("items"); throw new Exception("Disposed database survived."); } catch (ObjectDisposedException) { }
        handler.Verify();
    }

    [Test]
    public async Task Failed_creation_and_cancelled_initialization_can_be_retried()
    {
        using var handler = new CouchDbTestHandler();
        using var client = new HttpClient(handler);
        await using var database = new CouchDbLibrarianDatabase(Options(), client);
        handler.Expect(HttpMethod.Head, Db, status: HttpStatusCode.NotFound);
        handler.Expect(HttpMethod.Put, Db, status: HttpStatusCode.Forbidden);
        try { await database.GetContainer("items"); throw new Exception("Creation failure ignored."); } catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Forbidden) { }
        using var cancellation = new CancellationTokenSource();
        handler.BeforeSend = async token => { await cancellation.CancelAsync(); token.ThrowIfCancellationRequested(); };
        try { await database.GetContainer("items", cancellation.Token); throw new Exception("Cancellation ignored."); } catch (OperationCanceledException) { }
        handler.BeforeSend = null;
        handler.Expect(HttpMethod.Head, Db);
        await database.GetContainer("items");
        handler.Verify();
    }

    [Test]
    public async Task Preprovisioned_mode_never_creates_and_authentication_failures_do_not_look_missing()
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            using var handler = new CouchDbTestHandler();
            using var client = new HttpClient(handler);
            await using var database = new CouchDbLibrarianDatabase(Options(false), client);
            handler.Expect(HttpMethod.Head, Db, status: status);
            try { await database.GetContainer("items"); throw new Exception("Provisioning failure ignored."); } catch (HttpRequestException e) when (e.StatusCode == status) { }
            Check(handler.Calls == 1, "Unexpected provisioning request.");
            handler.Verify();
        }
    }

    [Test]
    public async Task Documents_use_native_revisions_without_metadata_or_lost_update_retries()
    {
        using var handler = new CouchDbTestHandler();
        using var client = new HttpClient(handler);
        await using var database = new CouchDbLibrarianDatabase(Options(), client);
        handler.Expect(HttpMethod.Head, Db);
        ILibrarianContainer container = await database.GetContainer("items", "org");
        handler.Expect(HttpMethod.Put, Db + Id, "{\"ok\":true,\"rev\":\"1-a\"}", HttpStatusCode.Created, (request, body) =>
        {
            using JsonDocument json = JsonDocument.Parse(body);
            Check(json.RootElement.GetProperty("_id").GetString() == "d-6f7267-61" && !json.RootElement.TryGetProperty("_rev", out _), "Incorrect create identity.");
        });
        await container.AddItem("a", Document);
        handler.Expect(HttpMethod.Get, Db + Id, Stored);
        LibrarianItem<string>? item = await container.GetItemWithVersion("a");
        Check(item?.Version == "1-a" && !item.Document.Contains("_rev", StringComparison.Ordinal), "Revision leaked into document.");
        handler.Expect(HttpMethod.Put, Db + Id, "{\"ok\":true,\"rev\":\"2-b\"}", inspect: (request, body) =>
        {
            using JsonDocument json = JsonDocument.Parse(body);
            Check(json.RootElement.GetProperty("_rev").GetString() == "1-a", "Revision condition missing.");
        });
        Check((await container.UpdateItemIfVersion("a", Document, "1-a"))?.Version == "2-b", "New revision lost.");
        handler.Expect(HttpMethod.Put, Db + Id, status: HttpStatusCode.Conflict);
        Check(await container.UpdateItemIfVersion("a", Document, "1-a") is null, "Stale update accepted.");
        handler.Expect(HttpMethod.Get, Db + Id, Stored);
        handler.Expect(HttpMethod.Put, Db + Id, status: HttpStatusCode.Conflict);
        try { await container.UpdateItem("a", Document); throw new Exception("Lost update ignored."); } catch (LibrarianConcurrencyException) { }
        handler.Expect(HttpMethod.Delete, Db + Id + "?rev=1-a", status: HttpStatusCode.Conflict);
        Check(!await container.DeleteItemIfVersion("a", "1-a"), "Stale delete accepted.");
        handler.Expect(HttpMethod.Get, Db + Id, "{\"error\":\"not_found\",\"reason\":\"missing\"}", HttpStatusCode.NotFound);
        Check(await container.GetItem("a") is null, "Missing document was returned.");
        handler.Verify();
    }

    [Test]
    public async Task Bulk_reads_preserve_order_duplicates_and_missing_documents()
    {
        using var handler = new CouchDbTestHandler();
        using var client = new HttpClient(handler);
        await using var database = new CouchDbLibrarianDatabase(Options(), client);
        handler.Expect(HttpMethod.Head, Db);
        ILibrarianContainer container = await database.GetContainer("items", "org");
        handler.Expect(HttpMethod.Post, Db + "/_all_docs?include_docs=true", "{\"rows\":[{\"id\":\"d-6f7267-61\",\"doc\":" + Stored + "},{\"key\":\"missing\",\"error\":\"not_found\"}]}");
        string?[] values = await container.GetItems(["a", "missing", "a"]);
        Check(values.Length == 3 && values[0] == values[2] && values[0] is not null && values[1] is null, "Bulk read order changed.");
        Check((await container.GetItems([])).Length == 0, "Empty bulk read failed.");
        handler.Verify();
    }

    [Test]
    public async Task Indexes_are_explicit_and_queries_are_scoped_and_do_not_fall_back()
    {
        using var handler = new CouchDbTestHandler();
        using var client = new HttpClient(handler);
        await using var database = new CouchDbLibrarianDatabase(Options(), client);
        handler.Expect(HttpMethod.Head, Db);
        ILibrarianContainer container = await database.GetContainer("items", "org");
        handler.Expect(HttpMethod.Post, Db + "/_index", "{\"result\":\"created\"}", inspect: (request, body) =>
        {
            using JsonDocument json = JsonDocument.Parse(body);
            Check(json.RootElement.GetProperty("index").GetProperty("fields").EnumerateArray().Select(v => v.GetString()).SequenceEqual(["partitionKey", "score_value", "_id"]), "Index scope or ordering missing.");
        });
        await container.EnsureIndex("score_value");
        await container.EnsureIndex("score_value");
        handler.Expect(HttpMethod.Post, Db + "/_find", "{\"docs\":[" + Stored + "],\"bookmark\":\"end\",\"execution_stats\":{\"total_keys_examined\":2}}", inspect: (request, body) =>
        {
            using JsonDocument json = JsonDocument.Parse(body);
            Check(!json.RootElement.GetProperty("allow_fallback").GetBoolean() && json.RootElement.GetProperty("selector").GetRawText().Contains("org", StringComparison.Ordinal), "Query can escape partition or scan.");
            Check(json.RootElement.GetProperty("limit").GetInt32() == 5, "Query ignored page size.");
        });
        var page = await container.FindRangeByIndex<NativeDocument>("score_value", 0, 2, take: 5);
        Check(page.Items.Count == 1 && page.Items[0].Score == 1 && page.IndexEntriesExamined == 2, "Query result failed.");
        handler.Expect(HttpMethod.Post, Db + "/_find", "{\"docs\":[],\"warning\":\"No matching index\"}");
        try { await container.ExistsByIndex("score_value", 1); throw new Exception("Index fallback accepted."); } catch (InvalidOperationException e) when (e.Message.StartsWith("CouchDB", StringComparison.Ordinal)) { }
        handler.Verify();
    }

    [Test]
    public async Task Unsupported_operations_and_invalid_documents_fail_before_writing()
    {
        using var handler = new CouchDbTestHandler();
        using var client = new HttpClient(handler);
        await using var database = new CouchDbLibrarianDatabase(Options(), client);
        handler.Expect(HttpMethod.Head, Db);
        ILibrarianContainer container = await database.GetContainer("items", "org");
        foreach (string json in new[] { "{}", Document.Replace("\"org\"", "\"other\""), Document.Replace("}", ",\"_rev\":\"bad\"}") })
            try { await container.AddItem("a", json); throw new Exception("Invalid document accepted."); } catch (ArgumentException) { }
        try { container.BuildQueryable<NativeDocument>(); throw new Exception("Unsupported LINQ accepted."); } catch (NotSupportedException) { }
        try { await database.Execute(new LibrarianBatch([])); throw new Exception("Non-atomic batch accepted."); } catch (NotSupportedException) { }
        Check(handler.Calls == 1, "Rejected operations performed I/O.");
        handler.Verify();
    }

    [Test]
    public async Task Waiting_initialization_can_cancel_without_cancelling_the_owner()
    {
        using var handler = new CouchDbTestHandler();
        using var client = new HttpClient(handler);
        await using var database = new CouchDbLibrarianDatabase(Options(), client);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.BeforeSend = async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        handler.Expect(HttpMethod.Head, Db);
        Task<ILibrarianContainer> owner = database.GetContainer("items").AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var cancellation = new CancellationTokenSource();
            Task<ILibrarianContainer> waiter = database.GetContainer("items", cancellation.Token).AsTask();
            await cancellation.CancelAsync();
            try { await waiter.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Gate waiter ignored cancellation."); } catch (OperationCanceledException) { }
        }
        finally { release.TrySetResult(); }
        ILibrarianContainer first = await owner.WaitAsync(TimeSpan.FromSeconds(5));
        Check(ReferenceEquals(first, await database.GetContainer("items")) && handler.Calls == 1, "Cancellation poisoned initialization.");
        handler.Verify();
    }

    [Test]
    public async Task Count_queries_follow_bookmarks_and_validate_bounds_before_io()
    {
        using var handler = new CouchDbTestHandler();
        using var client = new HttpClient(handler);
        await using var database = new CouchDbLibrarianDatabase(Options(), client);
        handler.Expect(HttpMethod.Head, Db);
        ILibrarianContainer container = await database.GetContainer("items");
        string docs = string.Join(',', Enumerable.Repeat("{\"_id\":\"a\"}", 256));
        handler.Expect(HttpMethod.Post, Db + "/_find", "{\"docs\":[" + docs + "],\"bookmark\":\"next\"}", inspect: (request, body) =>
        {
            using JsonDocument query = JsonDocument.Parse(body);
            Check(query.RootElement.GetProperty("fields")[0].GetString() == "_id", "Count fetched complete documents.");
        });
        handler.Expect(HttpMethod.Post, Db + "/_find", "{\"docs\":[{\"_id\":\"b\"}],\"bookmark\":\"end\"}", inspect: (request, body) =>
        {
            using JsonDocument query = JsonDocument.Parse(body);
            Check(query.RootElement.GetProperty("bookmark").GetString() == "next", "Count did not advance bookmark.");
        });
        Check(await container.CountRangeByIndex("score_value", 0, 300) == 257, "Count stopped at the first page.");
        try { await container.CountRangeByIndex("score_value", 2, 1); throw new Exception("Reversed bounds accepted."); } catch (ArgumentException) { }
        try { await container.CountRangeByIndex("score_value", 2, "a"); throw new Exception("Mixed bounds accepted."); } catch (ArgumentException) { }
        handler.Verify();
    }

    [Test]
    public async Task Keyed_registration_is_lazy_and_disposes_the_database()
    {
        using var handler = new CouchDbTestHandler();
        using var client = new HttpClient(handler);
        var services = new ServiceCollection();
        services.AddCouchDbLibrarianDatabaseAsSingleton("couch", _ => new CouchDbLibrarianDatabase(Options(), client));
        await using ServiceProvider provider = services.BuildServiceProvider();
        ILibrarianDatabase database = provider.GetRequiredKeyedService<ILibrarianDatabase>("couch");
        Check(ReferenceEquals(database, provider.GetRequiredKeyedService<ILibrarianDatabase>("couch")) && handler.Calls == 0, "DI registration was not lazy singleton.");
    }
}
