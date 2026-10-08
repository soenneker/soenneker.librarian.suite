using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Moq;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Cosmos;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CosmosBatchTests
{
    [Test]
    public async Task Native_conflicts_return_false_and_service_errors_propagate_without_replay(CancellationToken cancellationToken)
    {
        foreach (HttpStatusCode status in new[] { HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed, HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.ServiceUnavailable })
        {
            var store = new Mock<Container>(MockBehavior.Strict);
            store.SetupGet(c => c.Id).Returns("items");
            var transaction = new Mock<TransactionalBatch>(MockBehavior.Strict);
            var response = new Mock<TransactionalBatchResponse>();
            response.SetupGet(r => r.StatusCode).Returns(status);
            response.SetupGet(r => r.ErrorMessage).Returns("server failure");
            store.Setup(c => c.CreateTransactionalBatch(new PartitionKey("org-a"))).Returns(transaction.Object);
            transaction.Setup(t => t.DeleteItem("one", It.Is<TransactionalBatchItemRequestOptions>(o => o.IfMatchEtag == "version"))).Returns(transaction.Object);
            transaction.Setup(t => t.ExecuteAsync(It.IsAny<CancellationToken>())).ReturnsAsync(response.Object);
            await using var database = new CosmosLibrarianDatabase(store.Object);
            var batch = new LibrarianBatch([new LibrarianWrite("items", "org-a:one", null, "version")]);
            if (status is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
                Check(!await database.Execute(batch, cancellationToken: cancellationToken), "Conflict reported success.");
            else
            {
                try { await database.Execute(batch, cancellationToken: cancellationToken); throw new Exception("Service failure hidden."); }
                catch (CosmosException exception) when (exception.StatusCode == status) { }
            }
            transaction.Verify(t => t.ExecuteAsync(It.IsAny<CancellationToken>()), Times.Once);
        }
    }
    [Test]
    public async Task Oversized_batches_fail_before_provisioning(CancellationToken cancellationToken)
    {
        var store = new Mock<Container>(MockBehavior.Strict);
        await using var database = new CosmosLibrarianDatabase(store.Object);
        var writes = new LibrarianWrite[101];
        for (var i = 0; i < writes.Length; i++) writes[i] = new LibrarianWrite("items", "org-a:" + i, null);
        try { await database.Execute(new LibrarianBatch(writes), cancellationToken: cancellationToken); throw new Exception("Oversized batch accepted."); }
        catch (ArgumentException) { }
        store.VerifyNoOtherCalls();
    }
}
