using System;
using Soenneker.Blazor.Utils.ModuleImport;
using Soenneker.Blazor.Utils.ModuleImport.Abstract;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Browser;
using Soenneker.Librarian.IndexedDb;
using Soenneker.Librarian.LocalStorage;
using Soenneker.Librarian.SessionStorage;

namespace Soenneker.Librarian.Suite.Tests;

public class ClientBrowserProviderTests
{
    private static BrowserSnapshotLibrarianDatabase Open(int backend, IModuleImportUtil modules, string key = "account") => backend == 1
        ? new IndexedDbLibrarianDatabase(modules, NullLogger<IndexedDbLibrarianDatabase>.Instance, key)
        : backend == 2 ? new SessionStorageLibrarianDatabase(modules, NullLogger<SessionStorageLibrarianDatabase>.Instance, key)
        : new LocalStorageLibrarianDatabase(modules, NullLogger<LocalStorageLibrarianDatabase>.Instance, key);

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Roundtrip_batches_and_conflicting_owners(int backend, CancellationToken cancellationToken)
    {
        var storage = new Dictionary<string, string>();
        await using var firstModules = new ModuleImportUtil(new BrowserStorageRuntime(storage));
        var first = Open(backend, firstModules);
        var staleRuntime = new BrowserStorageRuntime(storage);
        await using var staleModules = new ModuleImportUtil(staleRuntime);
        var stale = Open(backend, staleModules);
        var a = await first.GetContainer("items", cancellationToken: cancellationToken);
        var b = await stale.GetContainer("items", cancellationToken: cancellationToken);
        await a.AddItem("one", "first", cancellationToken: cancellationToken);
        await first.Save(cancellationToken: cancellationToken);
        await b.AddItem("two", "stale", cancellationToken: cancellationToken);
        try { await stale.Save(cancellationToken: cancellationToken); throw new Exception("Conflict ignored"); }
        catch (LibrarianConcurrencyException) { }
        await stale.DiscardAsync();
        if (staleRuntime.Disposed) throw new Exception("Database disposed a shared module");
        await first.Execute(new LibrarianBatch([new LibrarianWrite("items", "three", "batch")]), cancellationToken: cancellationToken);
        await first.DisposeAsync();
        await using var reopenedModules = new ModuleImportUtil(new BrowserStorageRuntime(storage));
        await using var reopened = Open(backend, reopenedModules);
        var items = await reopened.GetContainer("items", cancellationToken: cancellationToken);
        if (await items.GetItem("one", cancellationToken: cancellationToken) != "first" || await items.GetItem("three", cancellationToken: cancellationToken) != "batch" || await items.GetItem("two", cancellationToken: cancellationToken) is not null)
            throw new Exception("Stored data incorrect");
        await using var other = Open(backend, reopenedModules, "other-account");
        if (await (await other.GetContainer("items", cancellationToken: cancellationToken)).GetItem("one", cancellationToken: cancellationToken) is not null) throw new Exception("Key isolation failed");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Failed_batch_does_not_publish_and_save_can_retry(int backend, CancellationToken cancellationToken)
    {
        var runtime = new BrowserStorageRuntime(new Dictionary<string, string>());
        await using var modules = new ModuleImportUtil(runtime);
        await using var db = Open(backend, modules);
        var items = await db.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("one", "before", cancellationToken: cancellationToken);
        await db.Save(cancellationToken: cancellationToken);
        runtime.FailWrite = true;
        try { await db.Execute(new LibrarianBatch([new LibrarianWrite("items", "one", "after")]), cancellationToken: cancellationToken); throw new Exception("Failure ignored"); }
        catch (JSException) { }
        if (await items.GetItem("one", cancellationToken: cancellationToken) != "before") throw new Exception("Failed batch published");
        runtime.FailWrite = false;
        await db.Execute(new LibrarianBatch([new LibrarianWrite("items", "one", "after")]), cancellationToken: cancellationToken);
        try { await db.GetContainer("items", new CancellationToken(true)); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
    }

    [Test]
    public async Task Browser_backends_share_the_scoped_module_importer(CancellationToken cancellationToken)
    {
        var runtime = new BrowserStorageRuntime(new Dictionary<string, string>());
        var modules = new ModuleImportUtil(runtime);
        try
        {
            await using var local = Open(0, modules);
            await using var indexed = Open(1, modules);
            await using var session = Open(2, modules);
            await local.GetContainer("items", cancellationToken: cancellationToken);
            var indexedItems = await indexed.GetContainer("items", cancellationToken: cancellationToken);
            await session.GetContainer("items", cancellationToken: cancellationToken);
            await local.DiscardAsync();
            if (runtime.Disposed || runtime.Imports != 1)
                throw new Exception("Browser backends must share the importer-owned module.");
            await indexedItems.AddItem("one", "value", cancellationToken: cancellationToken);
            await indexed.Save(cancellationToken: cancellationToken);
        }
        finally
        {
            await modules.DisposeAsync();
        }
        if (!runtime.Disposed)
            throw new Exception("The module importer did not release its module.");
    }
}
