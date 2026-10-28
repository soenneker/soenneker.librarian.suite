using System.Collections.Generic;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Cosmos.Repositories.Audits.Abstract;
using Soenneker.Cosmos.Repository.Dtos;
using Soenneker.Documents.Audit;
using Soenneker.Utils.UserContext.Abstract;
using Soenneker.Utils.BackgroundQueue.Abstract;
using Soenneker.Utils.MemoryStream.Abstract;
namespace Soenneker.Librarian.Cosmos;
public sealed class AuditsCosmosLibrarianRepository : CosmosLibrarianRepository<AuditDocument>, IAuditsRepository
{
    public override string ContainerName => "audits";
    public override bool AuditEnabled => false;
    public AuditsCosmosLibrarianRepository(ICosmosLibrarianDatabaseFactory database, IConfiguration config, ILogger<AuditsCosmosLibrarianRepository> logger,
        IUserContext userContext, IBackgroundQueue queue, IMemoryStreamUtil streams) : base(database, config, logger, userContext, queue, streams) { }
    public ValueTask<List<AuditDocument>> GetByEntity(string partitionKey, CosmosReadOptions? readOptions = null, CancellationToken cancellationToken = default) =>
        GetAllByPartitionKey(partitionKey, readOptions: readOptions, cancellationToken: cancellationToken);
    ValueTask IAuditsRepository.UpdateItem(string id, AuditDocument document, bool useQueue, bool excludeResponse, CosmosWriteOptions? writeOptions, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Audit records are immutable.");
    public override ValueTask<string> AddItem(AuditDocument document, bool useQueue = false, bool excludeResponse = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Use repository audit creation.");
}
