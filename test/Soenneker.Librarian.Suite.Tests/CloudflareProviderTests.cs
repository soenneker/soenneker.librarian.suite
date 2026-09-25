using System;
using System.IO;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Kiota.Http.HttpClientLibrary;
using Soenneker.Cloudflare.OpenApiClient;
using Soenneker.Cloudflare.R2;
using Soenneker.Cloudflare.Utils.Client.Abstract;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.D1;
using Soenneker.Librarian.D1.Registrars;
using Soenneker.Librarian.R2;
using Soenneker.Librarian.R2.Registrars;

namespace Soenneker.Librarian.Suite.Tests;

public class CloudflareProviderTests
{
    [Test]
    [Arguments("d1")]
    [Arguments("r2")]
    public async Task Save_unload_and_dispose_round_trip_through_cloudflare_clients(string provider)
    {
        using var fixture = new CloudflareFixture(provider);
        await using (ILibrarianDatabase db = fixture.Create())
        {
            ILibrarianContainer first = await db.GetContainer("items");
            await first.AddItem("ONE", "{\"amount\":1}");
            await first.EnsureIndex("amount");
            Check(await first.CountByIndex("amount", 1) == 1, "Index behavior changed.");
            Check(fixture.Handler.Snapshot is null, "Ordinary mutations should remain pending.");
            await db.Save();
            Check(fixture.Handler.Snapshot is not null, "Save did not persist.");
            Check(await db.UnloadContainer("items"), "Unload failed.");
            await (await db.GetContainer("other")).AddItem("two", "retained");
            await db.Save();
            Check(await (await db.GetContainer("items")).GetItem("one") == "{\"amount\":1}", "Unload lost data.");
            await (await db.GetContainer("items")).UpdateItem("one", "final");
        }
        await using ILibrarianDatabase reopened = fixture.Create();
        Check(await (await reopened.GetContainer("items")).GetItem("one") == "final", "Dispose did not flush.");
        Check(await (await reopened.GetContainer("other")).GetItem("two") == "retained", "Other container was lost.");
        Check(fixture.Clients.LastApiKey == "test-token", "Configured token was not used.");
    }

    [Test]
    [Arguments("d1")]
    [Arguments("r2")]
    public async Task Failed_save_and_batch_preserve_state_and_can_retry(string provider)
    {
        using var fixture = new CloudflareFixture(provider);
        await using ILibrarianDatabase db = fixture.Create();
        ILibrarianContainer items = await db.GetContainer("items");
        await items.AddItem("a", "before");
        await db.Save();
        string? original = fixture.Handler.Snapshot;
        fixture.Handler.FailWrites = true;
        try { await db.Execute(new([new("items", "a", "after"), new("other", "b", "new")])); throw new Exception("Expected batch failure."); }
        catch (InvalidDataException) { }
        Check(await items.GetItem("a") == "before" && await (await db.GetContainer("other")).GetItem("b") is null, "Failed batch was published.");
        Check(fixture.Handler.Snapshot == original, "Failed batch changed storage.");
        await items.UpdateItem("a", "pending");
        try { await db.Save(); throw new Exception("Expected save failure."); }
        catch (InvalidDataException) { }
        fixture.Handler.FailWrites = false;
        await db.Save();
        Check(fixture.Handler.Snapshot!.Contains("pending", StringComparison.Ordinal), "Retry lost pending data.");
        Check(!await db.Execute(new([new("items", "a", "wrong")], [new("items", "a", "before")])), "Stale condition committed.");
        Check(await db.Execute(new([new("items", "a", "after"), new("other", "b", "new")], [new("items", "a", "pending")])), "Batch failed.");
        Check(fixture.Handler.Snapshot!.Contains("after", StringComparison.Ordinal) && fixture.Handler.Snapshot.Contains("new", StringComparison.Ordinal), "Batch was not durable.");
    }

    [Test]
    [Arguments("d1")]
    [Arguments("r2")]
    public async Task Corrupt_storage_is_not_overwritten_and_load_can_retry(string provider)
    {
        using var fixture = new CloudflareFixture(provider);
        fixture.Handler.Snapshot = "null";
        await using ILibrarianDatabase db = fixture.Create();
        try { await db.GetContainer("items"); throw new Exception("Expected corrupt snapshot failure."); }
        catch (InvalidDataException) { }
        await db.Save();
        Check(fixture.Handler.Snapshot == "null", "Corrupt snapshot was overwritten.");
        fixture.Handler.Snapshot = "{}";
        await (await db.GetContainer("items")).AddItem("a", "recovered");
        await db.Save();
        Check(fixture.Handler.Snapshot.Contains("recovered", StringComparison.Ordinal), "Load did not retry.");
    }

    [Test]
    [Arguments("d1")]
    [Arguments("r2")]
    public async Task Cancellation_and_failed_disposal_keep_pending_changes(string provider)
    {
        using var fixture = new CloudflareFixture(provider);
        ILibrarianDatabase db = fixture.Create();
        ILibrarianContainer items = await db.GetContainer("items");
        await items.AddItem("a", "pending");
        try { await db.Save(new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        fixture.Handler.FailWrites = true;
        try { await db.DisposeAsync(); throw new Exception("Expected disposal failure."); }
        catch (InvalidDataException) { }
        fixture.Handler.FailWrites = false;
        await db.DisposeAsync();
        await db.DisposeAsync();
        Check(fixture.Handler.Snapshot!.Contains("pending", StringComparison.Ordinal), "Disposal retry lost changes.");
        try { await db.GetContainer("items"); throw new Exception("Disposed database accepted operation."); }
        catch (ObjectDisposedException) { }
    }

    [Test]
    public async Task D1_checks_statement_success_even_when_envelope_succeeds()
    {
        using var fixture = new CloudflareFixture("d1");
        await using ILibrarianDatabase db = fixture.Create();
        await (await db.GetContainer("items")).AddItem("a", "pending");
        fixture.Handler.FailStatement = true;
        try { await db.Save(); throw new Exception("Statement failure ignored."); }
        catch (InvalidDataException) { }
        finally { fixture.Handler.FailStatement = false; }
        Check(fixture.Handler.Snapshot is null, "Failed statement changed storage.");
    }

    [Test]
    [Arguments("d1")]
    [Arguments("r2")]
    public async Task Authorization_errors_are_not_treated_as_missing_storage(string provider)
    {
        using var fixture = new CloudflareFixture(provider);
        fixture.Handler.DenyReads = true;
        await using ILibrarianDatabase db = fixture.Create();
        bool failed = false;
        try { await db.GetContainer("items"); }
        catch (HttpRequestException) { failed = true; }
        catch (Microsoft.Kiota.Abstractions.ApiException) { failed = true; }
        Check(failed, "Authorization failure was swallowed.");
        await db.Save();
        Check(fixture.Handler.Writes == 0, "Failed load caused a write.");
    }

    [Test]
    public async Task D1_rejects_oversized_snapshots_before_sending_them()
    {
        using var fixture = new CloudflareFixture("d1");
        await using ILibrarianDatabase db = fixture.Create();
        ILibrarianContainer items = await db.GetContainer("items");
        await items.AddItem("large", new string('x', 1_900_000));
        try { await db.Save(); throw new Exception("Oversized snapshot was accepted."); }
        catch (InvalidOperationException) { }
        Check(fixture.Handler.Writes == 0, "Oversized snapshot reached Cloudflare.");
        await items.DeleteItem("large");
    }

    [Test]
    [Arguments("d1")]
    [Arguments("r2")]
    public async Task Registrars_resolve_configured_singletons_without_owning_clients(string provider)
    {
        using var fixture = new CloudflareFixture(provider);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(fixture.ClientUtil);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Librarian:D1:AccountId"] = "account",
            ["Librarian:D1:ApiKey"] = "test-token",
            ["Librarian:D1:DatabaseId"] = "database",
            ["Librarian:D1:Name"] = "name'with-quotes",
            ["Librarian:R2:AccountId"] = "account",
            ["Librarian:R2:ApiKey"] = "test-token",
            ["Librarian:R2:BucketName"] = "bucket"
        }).Build());
        if (provider == "d1") services.AddD1LibrarianDatabaseAsSingleton();
        else services.AddR2LibrarianDatabaseAsSingleton();
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        ILibrarianDatabase db = serviceProvider.GetRequiredService<ILibrarianDatabase>();
        Check(ReferenceEquals(db, serviceProvider.GetRequiredService<ILibrarianDatabase>()), "Provider is not a singleton.");
        await (await db.GetContainer("items")).AddItem("one", "registered");
        await db.Save();
        Check(fixture.Handler.Snapshot!.Contains("registered", StringComparison.Ordinal), "Registered provider did not save.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}

internal sealed class CloudflareFixture : IDisposable
{
    private readonly string _provider;
    private readonly HttpClient _http;
    private readonly HttpClientRequestAdapter _adapter;
    private readonly ICloudflareClientUtil _clientUtil;
    public ICloudflareClientUtil ClientUtil => _clientUtil;
    public readonly CloudflareHandler Handler = new();
    public readonly CloudflareClientProxy Clients;

    public CloudflareFixture(string provider)
    {
        _provider = provider;
        _http = new HttpClient(Handler);
        _adapter = new HttpClientRequestAdapter(new AnonymousAuthenticationProvider(), httpClient: _http);
        _clientUtil = DispatchProxy.Create<ICloudflareClientUtil, CloudflareClientProxy>();
        Clients = (CloudflareClientProxy)_clientUtil;
        Clients.Client = new CloudflareOpenApiClient(_adapter);
    }

    public ILibrarianDatabase Create() => _provider == "d1"
        ? new D1LibrarianDatabase("account", "test-token", "database", _clientUtil, NullLogger.Instance, "name'with-quotes")
        : new R2LibrarianDatabase("account", "bucket", "folder/librarian.json", new CloudflareR2Util(_clientUtil), NullLogger.Instance, "test-token");

    public void Dispose()
    {
        _adapter.Dispose();
        _http.Dispose();
    }
}

public class CloudflareClientProxy : DispatchProxy
{
    public CloudflareOpenApiClient Client = null!;
    public string? LastApiKey;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod!.Name != "Get") throw new NotSupportedException(targetMethod.Name);
        LastApiKey = args![0] as string;
        ((CancellationToken)args[^1]!).ThrowIfCancellationRequested();
        return ValueTask.FromResult(Client);
    }
}

internal sealed class CloudflareHandler : HttpMessageHandler
{
    public string? Snapshot;
    public bool FailWrites;
    public bool FailStatement;
    public bool DenyReads;
    public int Writes;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DenyReads) return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (request.RequestUri!.AbsolutePath.Contains("/d1/", StringComparison.Ordinal))
        {
            using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            string sql = body.RootElement.GetProperty("sql").GetString()!;
            if (sql.StartsWith("CREATE", StringComparison.Ordinal)) return Json("{\"success\":true,\"result\":[{\"success\":true,\"results\":[]}]}");
            JsonElement parameters = body.RootElement.GetProperty("params");
            if (parameters[0].GetString() != "name'with-quotes" || sql.Contains("name'with-quotes", StringComparison.Ordinal))
                throw new Exception("D1 logical name was not parameterized.");
            if (sql.StartsWith("SELECT", StringComparison.Ordinal))
                return Json("{\"success\":true,\"result\":[{\"success\":true,\"results\":" +
                    (Snapshot is null ? "[]" : "[{\"value\":" + JsonSerializer.Serialize(Snapshot) + "}]") + "}]}");
            if (FailWrites) return Json("{\"success\":false,\"errors\":[{\"message\":\"failure\"}]}");
            if (FailStatement) return Json("{\"success\":true,\"result\":[{\"success\":false,\"error\":\"failure\"}]}");
            Writes++;
            Snapshot = parameters[1].GetString();
            return Json("{\"success\":true,\"result\":[{\"success\":true,\"results\":[]}]}");
        }
        if (request.Method == HttpMethod.Get)
            return Snapshot is null ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") } : Json(Snapshot);
        if (request.Method != HttpMethod.Put) throw new Exception("Unexpected R2 method.");
        if (FailWrites) return Json("{\"success\":false,\"errors\":[{\"message\":\"failure\"}]}");
        if (request.Content!.Headers.ContentType?.MediaType != "application/json") throw new Exception("Incorrect R2 content type.");
        Writes++;
        Snapshot = await request.Content.ReadAsStringAsync(cancellationToken);
        return Json("{\"success\":true,\"errors\":[],\"result\":{}}");
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
