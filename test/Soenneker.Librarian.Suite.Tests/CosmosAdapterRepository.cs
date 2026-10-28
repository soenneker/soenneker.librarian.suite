using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Cosmos;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class CosmosAdapterRepository(ICosmosLibrarianDatabaseFactory database)
    : CosmosLibrarianRepository<NativeDocument>(database, new ConfigurationBuilder().Build(),
        NullLogger.Instance, null!, null!, null!)
{
    public override string ContainerName => "items";
    public override bool AuditEnabled => false;
}
