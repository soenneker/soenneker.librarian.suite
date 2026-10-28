using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Cosmos;

namespace Soenneker.Librarian.Suite.Tests;

internal sealed class ScopedCosmosAdapterRepository(ICosmosLibrarianDatabaseFactory database)
    : CosmosLibrarianRepository<NativeDocument>(database, new ConfigurationBuilder().Build(),
        NullLogger.Instance, null!, null!, null!)
{
    public override string ContainerName => "items";
    public override bool AuditEnabled => false;
    protected override bool HasScope => true;
    protected override bool InScope(NativeDocument document) => document.Name == "allowed";
    protected override IQueryable<NativeDocument> Scope(IQueryable<NativeDocument> query) => query.Where(row => row.Name == "allowed");
}
