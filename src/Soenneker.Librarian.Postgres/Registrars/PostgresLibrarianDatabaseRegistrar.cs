using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.Postgres.Registrars;

/// <summary>
/// Registers the Librarian PostgreSQL database provider.
/// </summary>
public static class PostgresLibrarianDatabaseRegistrar
{
    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a singleton service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddPostgresLibrarianDatabaseAsSingleton(this IServiceCollection services)
    {
        services.TryAddSingleton<ILibrarianDatabase, PostgresLibrarianDatabase>();

        return services;
    }

    /// <summary>
    /// Adds <see cref="ILibrarianDatabase"/> as a scoped service. <para/>
    /// </summary>
    /// <param name="services">Service collection that receives the registration.</param>
    /// <returns>The same service collection, so additional registrations can be chained.</returns>
    public static IServiceCollection AddPostgresLibrarianDatabaseAsScoped(this IServiceCollection services)
    {
        services.TryAddScoped<ILibrarianDatabase, PostgresLibrarianDatabase>();

        return services;
    }
}
