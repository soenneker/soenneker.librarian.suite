using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Blazor.Utils.ModuleImport;
using Soenneker.Librarian.LocalStorage;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class ContainerLookupTests
{
    [Test]
    public async Task Optional_container_and_persisted_empty_container_roundtrip(CancellationToken cancellationToken)
    {
        var storage = new Dictionary<string, string>();
        var logger = NullLogger<LocalStorageLibrarianDatabase>.Instance;
        await using var modules = new ModuleImportUtil(new BrowserStorageRuntime(storage));
        var db = new LocalStorageLibrarianDatabase(modules, logger, "lookup");
        var container = await db.GetOrCreateContainer("width", cancellationToken);
        if (await container.GetItem("width", cancellationToken) is not null)
            throw new Exception("An optional container must start empty.");
        await db.Save(cancellationToken);
        await db.Discard();
        await using var reopened = new LocalStorageLibrarianDatabase(modules, logger, "lookup");
        var empty = await reopened.GetExistingContainer("width", cancellationToken);
        await empty.AddItem("width", "320", cancellationToken);
        await reopened.Save(cancellationToken);
        await reopened.Discard();
        await using var restored = new LocalStorageLibrarianDatabase(modules, logger, "lookup");
        if (await (await restored.GetOrCreateContainer("width", cancellationToken)).GetItem("width", cancellationToken) != "320")
            throw new Exception("Saved width was not restored.");
    }

    [Test]
    public async Task Required_missing_container_fails_without_creating_it(CancellationToken cancellationToken)
    {
        var storage = new Dictionary<string, string>();
        var logger = NullLogger<LocalStorageLibrarianDatabase>.Instance;
        await using var modules = new ModuleImportUtil(new BrowserStorageRuntime(storage));
        await using var db = new LocalStorageLibrarianDatabase(modules, logger, "lookup");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await db.GetExistingContainer("missing", cancellationToken);
                throw new Exception("Missing required container was accepted.");
            }
            catch (KeyNotFoundException) { }
        }
        await db.Save(cancellationToken);
        if (storage.Count != 0)
            throw new Exception("Existing-only lookup created storage.");
        await db.GetContainer("legacy", cancellationToken);
    }
}
