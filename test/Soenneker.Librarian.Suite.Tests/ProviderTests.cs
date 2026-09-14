using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Core;
using Soenneker.Librarian.Memory.Registrars;
using Soenneker.Librarian.FileSystem.Registrars;

namespace Soenneker.Librarian.Suite.Tests;

public class ProviderTests
{
    [Test]
    public async Task Providers_agree_on_names_and_cancelled_cached_lookups()
    {
        string path = Path.Combine(Path.GetTempPath(), $"librarian-contract-{Guid.NewGuid():N}.json");
        try
        {
            foreach (bool useFile in new[] { false, true })
            {
                IServiceCollection collection = new ServiceCollection().AddLogging();
                collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Librarian:FileSystem:FilePath"] = path }).Build());
                if (useFile) collection.AddFileSystemLibrarianDatabaseAsSingleton();
                else collection.AddMemoryLibrarianDatabaseAsSingleton();
                await using ServiceProvider services = collection.BuildServiceProvider();
                var database = services.GetRequiredService<ILibrarianDatabase>();
                ILibrarianContainer lower = await database.GetContainer("items");
                ILibrarianContainer upper = await database.GetContainer("Items");
                if (ReferenceEquals(lower, upper)) throw new Exception("Container names were not case-sensitive");
                try { await database.GetContainer(" "); throw new Exception("Blank name accepted"); }
                catch (ArgumentException) { }
                try { await database.UnloadContainer(" "); throw new Exception("Blank unload name accepted"); }
                catch (ArgumentException) { }
                try { await database.GetContainer("items", new CancellationToken(true)); throw new Exception("Cached lookup ignored cancellation"); }
                catch (OperationCanceledException) { }
            }
        }
        finally { System.IO.File.Delete(path); }
    }

    [Test]
    public async Task Scoped_registrations_resolve_dependencies_and_isolate_database_lifetimes()
    {
        string path = Path.Combine(Path.GetTempPath(), $"librarian-scoped-{Guid.NewGuid():N}.json");
        try
        {
            foreach (bool useFile in new[] { false, true })
            {
                IServiceCollection collection = new ServiceCollection().AddLogging();
                collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Librarian:FileSystem:FilePath"] = path }).Build());
                if (useFile) collection.AddFileSystemLibrarianDatabaseAsScoped();
                else collection.AddMemoryLibrarianDatabaseAsScoped();
                await using ServiceProvider services = collection.BuildServiceProvider(new ServiceProviderOptions
                {
                    ValidateScopes = true,
                    ValidateOnBuild = true
                });
                ILibrarianDatabase first;
                await using (AsyncServiceScope scope = services.CreateAsyncScope())
                {
                    first = scope.ServiceProvider.GetRequiredService<ILibrarianDatabase>();
                    if (!ReferenceEquals(first, scope.ServiceProvider.GetRequiredService<ILibrarianDatabase>()))
                        throw new Exception("Database was not shared within its scope.");
                    await (await first.GetContainer("items")).AddItem("one", "value");
                }
                await using (AsyncServiceScope scope = services.CreateAsyncScope())
                {
                    var second = scope.ServiceProvider.GetRequiredService<ILibrarianDatabase>();
                    if (ReferenceEquals(first, second)) throw new Exception("Database was shared across scopes.");
                    string? value = (await (await second.GetContainer("items")).GetItem("one"));
                    if (value != (useFile ? "value" : null))
                        throw new Exception("Scope disposal did not preserve the provider's storage behavior.");
                }
            }
        }
        finally { System.IO.File.Delete(path); }
    }

    [Test]
    public async Task Memory_database_owns_containers_and_discards_unloaded_data()
    {
        await using ServiceProvider services = new ServiceCollection().AddLogging().AddMemoryLibrarianDatabaseAsSingleton().BuildServiceProvider();
        var database = services.GetRequiredService<ILibrarianDatabase>();
        ILibrarianContainer container = await database.GetContainer("items");
        await container.AddItem("one", "original");
        if (!ReferenceEquals(container, await database.GetContainer("items")))
            throw new Exception("Container ownership changed.");
        await database.Save();
        if (!await database.UnloadContainer("items")) throw new Exception("Unload failed.");
        try { await container.GetItem("one"); throw new Exception("Unloaded container remained usable."); }
        catch (ObjectDisposedException) { }
        if ((await (await database.GetContainer("items")).GetItem("one")) != null)
            throw new Exception("Unloaded memory data was retained.");
        try { await database.GetContainer("cancelled", new CancellationToken(true)); throw new Exception("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
    }

    [Test]
    public async Task Repository_contract_works_with_both_providers()
    {
        string path = Path.Combine(Path.GetTempPath(), $"librarian-provider-{Guid.NewGuid():N}.json");
        try
        {
            foreach (bool useFile in new[] { false, true })
            {
                IServiceCollection collection = new ServiceCollection().AddLogging();
                collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Librarian:FileSystem:FilePath"] = path }).Build());
                if (useFile) collection.AddFileSystemLibrarianDatabaseAsSingleton();
                else collection.AddMemoryLibrarianDatabaseAsSingleton();
                await using ServiceProvider services = collection.BuildServiceProvider();
                var database = services.GetRequiredService<ILibrarianDatabase>();
                var repository = new LibrarianRepository<ExampleDocument>(new ConfigurationBuilder().Build(),
                    NullLogger<LibrarianRepository<ExampleDocument>>.Instance, database, "items");
                await repository.AddItem(new ExampleDocument { Id = "one", Name = "first" });
                if ((await repository.GetItem("ONE"))?.Name != "first") throw new Exception("Lookup failed.");
                await repository.UpdateItem(new ExampleDocument { Id = "one", Name = "second" });
                if (repository.GetItems(await repository.BuildQueryable<ExampleDocument>()).Count != 1)
                    throw new Exception("Query failed.");
                try { await repository.UpdateItem(new ExampleDocument { Id = "missing" }); throw new Exception("Missing update succeeded."); }
                catch (KeyNotFoundException) { }
                await database.Save();
                await repository.DeleteItem("one");
                if (await repository.GetAll() != null) throw new Exception("Delete failed.");
            }
        }
        finally { System.IO.File.Delete(path); }
    }
}
