using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Soenneker.Cloudflare.Utils.Client.Registrars;
using Soenneker.Librarian.Abstractions;

namespace Soenneker.Librarian.D1.Registrars;

/// <summary>Registers the single-owner Cloudflare D1 snapshot provider.</summary>
public static class D1LibrarianDatabaseRegistrar
{
    /// <summary>Adds the D1 database and its Soenneker Cloudflare client as singleton services.</summary>
    /// <remarks>Requires Librarian:D1:AccountId, ApiKey, and DatabaseId. Name defaults to librarian.
    /// The D1 database must exist; the snapshot table is created automatically. Only one owner may use each name.</remarks>
    public static IServiceCollection AddD1LibrarianDatabaseAsSingleton(this IServiceCollection services)
    {
        services.AddCloudflareClientUtilAsSingleton().TryAddSingleton<ILibrarianDatabase, D1LibrarianDatabase>();
        return services;
    }
}
