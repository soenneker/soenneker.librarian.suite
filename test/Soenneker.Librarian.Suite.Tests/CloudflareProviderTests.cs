using Soenneker.Utils.Json;
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
using Soenneker.Cloudflare.D1;
using Soenneker.Cloudflare.Workers.Kv;
using Soenneker.Cloudflare.Utils.Client.Abstract;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.D1;
using Soenneker.Librarian.D1.Registrars;
using Soenneker.Librarian.Kv;
using Soenneker.Librarian.Kv.Registrars;
using Soenneker.Librarian.R2;
using Soenneker.Librarian.R2.Registrars;

namespace Soenneker.Librarian.Suite.Tests;

public class CloudflareProviderTests
{
    [Test]
    [Arguments("d1")]
    [Arguments("r2")]
    [Arguments("kv")]
    public async ValueTask Save_unload_and_dispose_round_trip_through_cloudflare_clients(string provider, CancellationToken cancellationToken)
    {
        using var fixture = new CloudflareFixture(provider);
        await using (ILibrarianDatabase db = fixture.Create())
        {
            ILibrarianContainer first = await db.GetContainer("items", cancellationToken: cancellationToken);
            await first.AddItem("ONE", "{\"amount\":1}", cancellationToken: cancellationToken);
            await first.EnsureIndex("amount", cancellationToken: cancellationToken);
            Check(await first.CountByIndex("amount", 1, cancellationToken: cancellationToken) == 1, "Index behavior changed.");
            Check(fixture.Handler.Snapshot is null, "Ordinary mutations should remain pending.");
            await db.Save(cancellationToken: cancellationToken);
            Check(fixture.Handler.Snapshot is not null, "Save did not persist.");
            Check(await db.UnloadContainer("items", cancellationToken: cancellationToken), "Unload failed.");
            await (await db.GetContainer("other", cancellationToken: cancellationToken)).AddItem("two", "retained", cancellationToken: cancellationToken);
            await db.Save(cancellationToken: cancellationToken);
            Check(await (await db.GetContainer("items", cancellationToken: cancellationToken)).GetItem("one", cancellationToken: cancellationToken) == "{\"amount\":1}", "Unload lost data.");
            await (await db.GetContainer("items", cancellationToken: cancellationToken)).UpdateItem("one", "final", cancellationToken: cancellationToken);
        }
        await using ILibrarianDatabase reopened = fixture.Create();
        Check(await (await reopened.GetContainer("items", cancellationToken: cancellationToken)).GetItem("one", cancellationToken: cancellationToken) == "final", "Dispose did not flush.");
        Check(await (await reopened.GetContainer("other", cancellationToken: cancellationToken)).GetItem("two", cancellationToken: cancellationToken) == "retained", "Other container was lost.");
        Check(fixture.Clients.LastApiKey == "test-token", "Configured token was not used.");
    }

    [Test]
    [Arguments("d1")]
    [Arguments("r2")]
    [Arguments("kv")]
    public async ValueTask Failed_save_and_batch_preserve_state_and_can_retry(string provider, CancellationToken cancellationToken)
    {
        using var fixture = new CloudflareFixture(provider);
        await using ILibrarianDatabase db = fixture.Create();
        ILibrarianContainer items = await db.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("a", "before", cancellationToken: cancellationToken);
        await db.Save(cancellationToken: cancellationToken);
        string? original = fixture.Handler.Snapshot;
        fixture.Handler.FailWrites = true;
        try { await db.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", "after"), new LibrarianWrite("other", "b", "new")]), cancellationToken: cancellationToken); throw new Exception("Expected batch failure."); }
        catch (InvalidDataException) { }
        Check(await items.GetItem("a", cancellationToken: cancellationToken) == "before" && await (await db.GetContainer("other", cancellationToken: cancellationToken)).GetItem("b", cancellationToken: cancellationToken) is null, "Failed batch was published.");
        Check(fixture.Handler.Snapshot == original, "Failed batch changed storage.");
        await items.UpdateItem("a", "pending", cancellationToken: cancellationToken);
        try { await db.Save(cancellationToken: cancellationToken); throw new Exception("Expected save failure."); }
        catch (InvalidDataException) { }
        fixture.Handler.FailWrites = false;
        await db.Save(cancellationToken: cancellationToken);
        Check(fixture.Handler.Snapshot!.Contains("pending", StringComparison.Ordinal), "Retry lost pending data.");
        Check(!await db.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", "wrong")], [new LibrarianCondition("items", "a", "before")]), cancellationToken: cancellationToken), "Stale condition committed.");
        Check(await db.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", "after"), new LibrarianWrite("other", "b", "new")], [new LibrarianCondition("items", "a", "pending")]), cancellationToken: cancellationToken), "Batch failed.");
        Check(fixture.Handler.Snapshot!.Contains("after", StringComparison.Ordinal) && fixture.Handler.Snapshot.Contains("new", StringComparison.Ordinal), "Batch was not durable.");
    }

    [Test]
    [Arguments("d1")]
    [Arguments("r2")]
    [Arguments("kv")]
    public async ValueTask Corrupt_storage_is_not_overwritten_and_load_can_retry(string provider, CancellationToken cancellationToken)
    {
        using var fixture = new CloudflareFixture(provider);
        fixture.Handler.Snapshot = "null";
        await using ILibrarianDatabase db = fixture.Create();
        try { await db.GetContainer("items", cancellationToken: cancellationToken); throw new Exception("Expected corrupt snapshot failure."); }
        catch (InvalidDataException) { }
        await db.Save(cancellationToken: cancellationToken);
        Check(fixture.Handler.Snapshot == "null", "Corrupt snapshot was overwritten.");
        fixture.Handler.Snapshot = "{}";
        await (await db.GetContainer("items", cancellationToken: cancellationToken)).AddItem("a", "recovered", cancellationToken: cancellationToken);
        await db.Save(cancellationToken: cancellationToken);
        Check(fixture.Handler.Snapshot.Contains("recovered", StringComparison.Ordinal), "Load did not retry.");
    }

    [Test]
    [Arguments("d1")]
    [Arguments("r2")]
    [Arguments("kv")]
    public async ValueTask Cancellation_and_failed_disposal_keep_pending_changes(string provider, CancellationToken cancellationToken)
    {
        using var fixture = new CloudflareFixture(provider);
        ILibrarianDatabase db = fixture.Create();
        ILibrarianContainer items = await db.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("a", "pending", cancellationToken: cancellationToken);
        try { await db.Save(new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        fixture.Handler.FailWrites = true;
        try { await db.DisposeAsync(); throw new Exception("Expected disposal failure."); }
        catch (InvalidDataException) { }
        fixture.Handler.FailWrites = false;
        await db.DisposeAsync();
        await db.DisposeAsync();
        Check(fixture.Handler.Snapshot!.Contains("pending", StringComparison.Ordinal), "Disposal retry lost changes.");
        try { await db.GetContainer("items", cancellationToken: cancellationToken); throw new Exception("Disposed database accepted operation."); }
        catch (ObjectDisposedException) { }
    }

    [Test]
    public async ValueTask D1_checks_statement_success_even_when_envelope_succeeds(CancellationToken cancellationToken)
    {
        using var fixture = new CloudflareFixture("d1");
        await using ILibrarianDatabase db = fixture.Create();
        await (await db.GetContainer("items", cancellationToken: cancellationToken)).AddItem("a", "pending", cancellationToken: cancellationToken);
        fixture.Handler.FailStatement = true;
        try { await db.Save(cancellationToken: cancellationToken); throw new Exception("Statement failure ignored."); }
        catch (InvalidDataException) { }
        finally { fixture.Handler.FailStatement = false; }
        Check(fixture.Handler.Snapshot is null, "Failed statement changed storage.");
    }

    [Test]
    [Arguments("d1")]
    [Arguments("r2")]
    [Arguments("kv")]
    public async ValueTask Authorization_errors_are_not_treated_as_missing_storage(string provider, CancellationToken cancellationToken)
    {
        using var fixture = new CloudflareFixture(provider);
        fixture.Handler.DenyReads = true;
        await using ILibrarianDatabase db = fixture.Create();
        var failed = false;
        try { await db.GetContainer("items", cancellationToken: cancellationToken); }
        catch (HttpRequestException) { failed = true; }
        catch (Microsoft.Kiota.Abstractions.ApiException) { failed = true; }
        catch (InvalidOperationException) when (provider == "kv") { failed = true; }
        Check(failed, "Authorization failure was swallowed.");
        await db.Save(cancellationToken: cancellationToken);
        Check(fixture.Handler.Writes == 0, "Failed load caused a write.");
    }

    [Test]
    public async ValueTask D1_rejects_oversized_snapshots_before_sending_them(CancellationToken cancellationToken)
    {
        using var fixture = new CloudflareFixture("d1");
        await using ILibrarianDatabase db = fixture.Create();
        ILibrarianContainer items = await db.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("large", new string('x', 1_900_000), cancellationToken: cancellationToken);
        try { await db.Save(cancellationToken: cancellationToken); throw new Exception("Oversized snapshot was accepted."); }
        catch (InvalidOperationException) { }
        Check(fixture.Handler.Writes == 0, "Oversized snapshot reached Cloudflare.");
        await items.DeleteItem("large", cancellationToken: cancellationToken);
    }

    [Test]
    [Arguments("d1")]
    [Arguments("r2")]
    [Arguments("kv")]
    public async ValueTask Registrars_resolve_configured_singletons_without_owning_clients(string provider, CancellationToken cancellationToken)
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
            ["Librarian:R2:BucketName"] = "bucket",
            ["Librarian:Kv:AccountId"] = "account",
            ["Librarian:Kv:ApiKey"] = "test-token",
            ["Librarian:Kv:NamespaceId"] = "namespace"
        }).Build());
        if (provider == "d1") services.AddD1LibrarianDatabaseAsSingleton();
        else if (provider == "kv") services.AddKvLibrarianDatabaseAsSingleton();
        else services.AddR2LibrarianDatabaseAsSingleton();
        await using ServiceProvider serviceProvider = services.BuildServiceProvider();
        var db = serviceProvider.GetRequiredService<ILibrarianDatabase>();
        Check(ReferenceEquals(db, serviceProvider.GetRequiredService<ILibrarianDatabase>()), "Provider is not a singleton.");
        await (await db.GetContainer("items", cancellationToken: cancellationToken)).AddItem("one", "registered", cancellationToken: cancellationToken);
        await db.Save(cancellationToken: cancellationToken);
        Check(fixture.Handler.Snapshot!.Contains("registered", StringComparison.Ordinal), "Registered provider did not save.");
    }

    [Test]
    public async ValueTask D1_initialization_retries_after_failure_and_runs_once_after_success(CancellationToken cancellationToken)
    {
        using var fixture = new CloudflareFixture("d1");
        await using ILibrarianDatabase db = fixture.Create();
        fixture.Handler.FailStatement = true;
        try { await db.GetContainer("items", cancellationToken: cancellationToken); throw new Exception("Initialization failure ignored."); }
        catch (InvalidDataException) { }
        fixture.Handler.FailStatement = false;
        // Force the subsequent snapshot load to fail after schema initialization succeeds.
        fixture.Handler.Snapshot = "null";
        try { await db.GetContainer("items", cancellationToken: cancellationToken); throw new Exception("Corrupt snapshot accepted."); }
        catch (InvalidDataException) { }
        fixture.Handler.Snapshot = "{}";
        await db.GetContainer("items", cancellationToken: cancellationToken);
        await db.GetContainer("other", cancellationToken: cancellationToken);
        Check(fixture.Handler.SchemaAttempts == 2, "Initialization did not retry or repeated after success.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
