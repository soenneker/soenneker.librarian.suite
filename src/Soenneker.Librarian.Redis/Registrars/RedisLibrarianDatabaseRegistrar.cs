using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Redis.Client.Registrars;

namespace Soenneker.Librarian.Redis.Registrars;

/// <summary>
/// Registers the Librarian Redis database provider.
/// </summary>
public static class RedisLibrarianDatabaseRegistrar
{
    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a singleton service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddRedisLibrarianDatabaseAsSingleton(this IServiceCollection services)
    {
        services.AddRedisClientAsSingleton()
                .TryAddSingleton<ILibrarianDatabase, RedisLibrarianDatabase>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a scoped service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddRedisLibrarianDatabaseAsScoped(this IServiceCollection services)
    {
        services.AddRedisClientAsScoped()
                .TryAddScoped<ILibrarianDatabase, RedisLibrarianDatabase>();

        return services;
    }
}
