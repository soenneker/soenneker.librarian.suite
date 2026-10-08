using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using Azure;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.AzureBlob.Registrars;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

public class AzureBlobProviderTests
{
    [Test]
    public async Task Save_unload_and_dispose_round_trip(CancellationToken cancellationToken)
    {
        var fixture = new AzureBlobFixture();
        await using (var db = fixture.Open())
        {
            var items = await db.GetContainer("items", cancellationToken: cancellationToken);
            await items.AddItem("a", "first", cancellationToken: cancellationToken);
            Check(fixture.Snapshot is null, "Write persisted before Save.");
            await db.Save(cancellationToken: cancellationToken);
            await db.Save(cancellationToken: cancellationToken);
            Check(fixture.Writes == 1, "Unchanged save wrote again.");
            await items.UpdateItem("a", "unloaded", cancellationToken: cancellationToken);
            await db.UnloadContainer("items", cancellationToken: cancellationToken);
            Check(await (await db.GetContainer("items", cancellationToken: cancellationToken)).GetItem("a", cancellationToken: cancellationToken) == "unloaded", "Unload lost data.");
            await (await db.GetContainer("other", cancellationToken: cancellationToken)).AddItem("b", "disposed", cancellationToken: cancellationToken);
        }
        await using var reopened = fixture.Open();
        Check(await (await reopened.GetContainer("items", cancellationToken: cancellationToken)).GetItem("a", cancellationToken: cancellationToken) == "unloaded", "Reopen lost data.");
        Check(await (await reopened.GetContainer("other", cancellationToken: cancellationToken)).GetItem("b", cancellationToken: cancellationToken) == "disposed", "Dispose did not save.");
    }

    [Test]
    public async Task Failed_save_and_batch_can_retry_without_publishing(CancellationToken cancellationToken)
    {
        var fixture = new AzureBlobFixture();
        await using var db = fixture.Open();
        var items = await db.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("a", "before", cancellationToken: cancellationToken);
        await db.Save(cancellationToken: cancellationToken);
        fixture.FailWrites = true;
        try
        {
            await db.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", "after")]), cancellationToken: cancellationToken);
            throw new Exception("Expected write failure.");
        }
        catch (RequestFailedException exception) when (exception.Status == 503) { }
        Check(await items.GetItem("a", cancellationToken: cancellationToken) == "before", "Failed batch was published.");
        await items.UpdateItem("a", "pending", cancellationToken: cancellationToken);
        try { await db.Save(cancellationToken: cancellationToken); throw new Exception("Expected save failure."); }
        catch (RequestFailedException exception) when (exception.Status == 503) { }
        fixture.FailWrites = false;
        await db.Save(cancellationToken: cancellationToken);
        Check(fixture.Snapshot!.Contains("pending", StringComparison.Ordinal), "Retry lost pending changes.");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Stale_owner_cannot_overwrite_snapshot(bool existing, CancellationToken cancellationToken)
    {
        var fixture = new AzureBlobFixture { Snapshot = existing ? "{}" : null };
        await using var first = fixture.Open();
        var second = fixture.Open();
        await (await first.GetContainer("items", cancellationToken: cancellationToken)).AddItem("a", "winner", cancellationToken: cancellationToken);
        await (await second.GetContainer("items", cancellationToken: cancellationToken)).AddItem("b", "stale", cancellationToken: cancellationToken);
        await first.Save(cancellationToken: cancellationToken);
        try { await second.Save(cancellationToken: cancellationToken); throw new Exception("Expected concurrency conflict."); }
        catch (RequestFailedException exception) when (exception.Status == 412) { }
        await second.DiscardAndDispose();
        Check(fixture.Writes == 1 && !fixture.Snapshot!.Contains("stale", StringComparison.Ordinal), "Stale owner overwrote storage.");
    }

    [Test]
    public async Task Missing_container_and_corrupt_snapshot_are_not_treated_as_empty(CancellationToken cancellationToken)
    {
        var fixture = new AzureBlobFixture { ReadError = "ContainerNotFound" };
        await using var db = fixture.Open();
        try { await db.GetContainer("items", cancellationToken: cancellationToken); throw new Exception("Expected missing container error."); }
        catch (RequestFailedException exception) when (exception.ErrorCode == "ContainerNotFound") { }
        fixture.ReadError = null;
        fixture.Snapshot = "null";
        try { await db.GetContainer("items", cancellationToken: cancellationToken); throw new Exception("Expected invalid snapshot error."); }
        catch (InvalidDataException) { }
        await db.Save(cancellationToken: cancellationToken);
        Check(fixture.Writes == 0, "Failed load overwrote storage.");
        fixture.Snapshot = "{}";
        await (await db.GetContainer("items", cancellationToken: cancellationToken)).AddItem("a", "recovered", cancellationToken: cancellationToken);
        await db.Save(cancellationToken: cancellationToken);
        Check(fixture.Writes == 1, "Load did not recover.");
    }

    [Test]
    public async Task Registrations_support_default_and_keyed_singletons(CancellationToken cancellationToken)
    {
        var first = new AzureBlobFixture();
        var second = new AzureBlobFixture();
        var services = new ServiceCollection();
        services.AddAzureBlobLibrarianDatabaseAsSingleton(_ => first.Open());
        services.AddAzureBlobLibrarianDatabaseAsSingleton("other", _ => second.Open());
        await using var provider = services.BuildServiceProvider();
        var db = provider.GetRequiredService<ILibrarianDatabase>();
        var keyed = provider.GetRequiredKeyedService<ILibrarianDatabase>("other");
        Check(ReferenceEquals(db, provider.GetRequiredService<ILibrarianDatabase>()), "Default registration is not singleton.");
        Check(!ReferenceEquals(db, keyed), "Keyed registration shares the default database.");
        await (await keyed.GetContainer("items", cancellationToken: cancellationToken)).AddItem("a", "keyed", cancellationToken: cancellationToken);
        await keyed.Save(cancellationToken: cancellationToken);
        Check(first.Snapshot is null && second.Snapshot is not null, "Keyed storage is not isolated.");
    }

    [Test]
    public async Task Configuration_registration_resolves_without_network_access(CancellationToken cancellationToken)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Librarian:AzureBlob:ConnectionString"] = "UseDevelopmentStorage=true",
                ["Librarian:AzureBlob:ContainerName"] = "librarian"
            }).Build());
        services.AddAzureBlobLibrarianDatabaseAsSingleton();
        services.AddAzureBlobLibrarianDatabaseAsSingleton("configured");
        await using var provider = services.BuildServiceProvider();
        Check(provider.GetRequiredService<ILibrarianDatabase>() is AzureBlob.AzureBlobLibrarianDatabase,
            "Configuration registration did not resolve.");
        Check(provider.GetRequiredKeyedService<ILibrarianDatabase>("configured") is AzureBlob.AzureBlobLibrarianDatabase,
            "Keyed configuration registration did not resolve.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
