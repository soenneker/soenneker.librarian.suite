using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Memory.Registrars;
using Soenneker.Librarian.Redis;
using Soenneker.Librarian.Redis.Registrars;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class KeyedRegistrationTests
{
    [Test]
    public async Task Named_databases_coexist_and_memory_data_is_isolated()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryLibrarianDatabaseAsSingleton();
        services.AddMemoryLibrarianDatabaseAsSingleton("first");
        services.AddMemoryLibrarianDatabaseAsSingleton("second");
        services.AddRedisLibrarianDatabaseAsSingleton("redis", _ =>
            new RedisLibrarianDatabase("test", _ => throw new InvalidOperationException("No connection should be opened.")));
        // Duplicate keys preserve the original registration.
        services.AddMemoryLibrarianDatabaseAsSingleton("first", _ => throw new Exception("Duplicate replaced original."));
        await using ServiceProvider provider = services.BuildServiceProvider();
        ILibrarianDatabase first = provider.GetRequiredKeyedService<ILibrarianDatabase>("first");
        ILibrarianDatabase second = provider.GetRequiredKeyedService<ILibrarianDatabase>("second");
        if (!ReferenceEquals(first, provider.GetRequiredKeyedService<ILibrarianDatabase>("first")))
            throw new Exception("Singleton was not reused.");
        if (ReferenceEquals(first, provider.GetRequiredService<ILibrarianDatabase>()))
            throw new Exception("Default registration was replaced.");
        if (provider.GetRequiredKeyedService<ILibrarianDatabase>("redis") is not RedisLibrarianDatabase)
            throw new Exception("Wrong provider resolved.");
        await (await first.GetContainer("items")).AddItem("one", "value");
        if (await (await second.GetContainer("items")).CountItems() != 0)
            throw new Exception("Named databases share memory data.");
    }

    [Test]
    public async Task Named_scoped_databases_are_reused_only_within_the_scope()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryLibrarianDatabaseAsScoped("scoped");
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using AsyncServiceScope first = provider.CreateAsyncScope();
        await using AsyncServiceScope second = provider.CreateAsyncScope();
        ILibrarianDatabase database = first.ServiceProvider.GetRequiredKeyedService<ILibrarianDatabase>("scoped");
        if (!ReferenceEquals(database, first.ServiceProvider.GetRequiredKeyedService<ILibrarianDatabase>("scoped")) ||
            ReferenceEquals(database, second.ServiceProvider.GetRequiredKeyedService<ILibrarianDatabase>("scoped")))
            throw new Exception("Scoped lifetime was not respected.");
    }
}
