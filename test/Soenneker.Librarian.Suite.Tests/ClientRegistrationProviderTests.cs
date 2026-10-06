using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Soenneker.Librarian.SessionStorage;
using Soenneker.Librarian.SessionStorage.Registrars;
using Microsoft.JSInterop;
using Microsoft.Maui.Storage;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.IndexedDb;
using Soenneker.Librarian.IndexedDb.Registrars;
using Soenneker.Librarian.LocalStorage;
using Soenneker.Librarian.LocalStorage.Registrars;
using Soenneker.Librarian.Maui.Secure;
using Soenneker.Librarian.Maui.Secure.Registrars;

namespace Soenneker.Librarian.Suite.Tests;

public class ClientRegistrationProviderTests
{
    [Test]
    public async Task Scoped_browser_and_keyed_mobile_registrations_resolve_without_platform_io()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Librarian:LocalStorage:Key"] = "local",
                ["Librarian:IndexedDb:Key"] = "indexed-account",
                ["Librarian:SessionStorage:Key"] = "session-account"
            }).Build());
        services.AddScoped<IJSRuntime>(_ => new BrowserStorageRuntime(new Dictionary<string, string>()));
        services.AddSingleton<ISecureStorage>(new SecureStorageStub());
        services.AddLocalStorageLibrarianDatabaseAsScoped();
        services.AddIndexedDbLibrarianDatabaseAsScoped("indexed");
        services.AddSessionStorageLibrarianDatabaseAsScoped("session");
        services.AddMauiSecureLibrarianDatabaseAsSingleton("mobile", "app/user/tenant", System.IO.Path.GetTempPath());
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var one = first.ServiceProvider.GetRequiredService<ILibrarianDatabase>();
        if (one is not LocalStorageLibrarianDatabase || !ReferenceEquals(one, first.ServiceProvider.GetRequiredService<ILibrarianDatabase>()))
            throw new Exception("Scoped registration failed");
        if (ReferenceEquals(one, second.ServiceProvider.GetRequiredService<ILibrarianDatabase>())) throw new Exception("Scopes leaked");
        if (first.ServiceProvider.GetRequiredKeyedService<ILibrarianDatabase>("indexed") is not IndexedDbLibrarianDatabase)
            throw new Exception("IndexedDb registration failed");
        if (first.ServiceProvider.GetRequiredKeyedService<ILibrarianDatabase>("session") is not SessionStorageLibrarianDatabase)
            throw new Exception("SessionStorage registration failed");
        var mobile = provider.GetRequiredKeyedService<ILibrarianDatabase>("mobile");
        if (mobile is not MauiSecureLibrarianDatabase || !ReferenceEquals(mobile, first.ServiceProvider.GetRequiredKeyedService<ILibrarianDatabase>("mobile")))
            throw new Exception("Mobile singleton registration failed");
    }
}
