using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Storage;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Maui.Secure.Registrars;

/// <summary>Registers encrypted, single-owner mobile snapshot storage.</summary>
public static class MauiSecureLibrarianDatabaseRegistrar
{
    /// <summary>Registers a database for a stable application/user/tenant scope. Supply ISecureStorage or use MAUI's default.</summary>
    /// <remarks>Save explicitly after important writes and before suspension. Dispose the owner before switching accounts;
    /// use DeleteStorage after disposal to erase a signed-out account. A scope must have only one owner.</remarks>
    public static IServiceCollection AddMauiSecureLibrarianDatabaseAsSingleton(this IServiceCollection services, string scope, string? directoryPath = null)
    {
        System.ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        services.TryAddSingleton<ISecureStorage>(_ => SecureStorage.Default);
        services.TryAddSingleton<ILibrarianDatabase>(provider => new MauiSecureLibrarianDatabase(scope,
            provider.GetRequiredService<ISecureStorage>(), provider.GetRequiredService<ILogger<MauiSecureLibrarianDatabase>>(), directoryPath));
        return services;
    }

    /// <summary>Adds an independently keyed singleton for a fixed account scope. Distinct DI keys must use distinct storage scopes.</summary>
    public static IServiceCollection AddMauiSecureLibrarianDatabaseAsSingleton(this IServiceCollection services, object serviceKey, string scope,
        string? directoryPath = null)
    {
        System.ArgumentNullException.ThrowIfNull(serviceKey);
        System.ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        services.TryAddSingleton<ISecureStorage>(_ => SecureStorage.Default);
        services.TryAddKeyedSingleton<ILibrarianDatabase>(serviceKey, (provider, _) => new MauiSecureLibrarianDatabase(scope,
            provider.GetRequiredService<ISecureStorage>(), provider.GetRequiredService<ILogger<MauiSecureLibrarianDatabase>>(), directoryPath));
        return services;
    }
}
