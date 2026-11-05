using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Moq;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Cosmos;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CosmosRevisionMutationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Conflicts_retry_only_when_the_section_revision_is_unchanged(bool sectionChanged, CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        int reads = 0, writes = 0, callbacks = 0;
        fixture.Store.Setup(c => c.ReadItemStreamAsync("one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                reads++;
                return Response(HttpStatusCode.OK, $"{{\"id\":\"one\",\"partitionKey\":\"org\",\"score_value\":{(sectionChanged && reads > 1 ? 1 : 0)},\"name\":\"fresh-{reads}\"}}", "read-" + reads);
            });
        fixture.Store.Setup(c => c.ReplaceItemStreamAsync(It.IsAny<Stream>(), "one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Stream _, string _, PartitionKey _, ItemRequestOptions options, CancellationToken _) =>
            {
                Check(options.IfMatchEtag == "read-" + reads, "ETag did not follow the fresh read.");
                return Response(++writes == 1 ? HttpStatusCode.PreconditionFailed : HttpStatusCode.OK, etag: "saved");
            });
        var repository = fixture.Resolve<CosmosAdapterRepository>();
        try
        {
            NativeDocument? result = await repository.MutateItemAtRevision("org:one", 0, d => d.Score,
                (d, revision) => d.Score = checked((int)revision), d => { callbacks++; }, cancellationToken: cancellationToken);
            Check(!sectionChanged && reads == 2 && writes == 2 && callbacks == 2 && result?.Score == 1 && result.Name == "fresh-2",
                "Unrelated concurrent change was lost or revision was advanced more than once.");
        }
        catch (LibrarianRevisionConflictException)
        {
            Check(sectionChanged && reads == 2 && writes == 1 && callbacks == 1, "Stale mutation was retried.");
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Retry_budget_is_bounded_and_uncertain_failures_are_not_retried(bool uncertain, CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        int writes = 0;
        fixture.Store.Setup(c => c.ReadItemStreamAsync("one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(HttpStatusCode.OK, "{\"id\":\"one\",\"partitionKey\":\"org\",\"score_value\":0}", "version"));
        fixture.Store.Setup(c => c.ReplaceItemStreamAsync(It.IsAny<Stream>(), "one", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                writes++;
                if (uncertain) throw new IOException("Response lost after dispatch.");
                return Response(HttpStatusCode.PreconditionFailed);
            });
        try
        {
            await fixture.Resolve<CosmosAdapterRepository>().MutateItemAtRevision("org:one", 0, d => d.Score,
                (d, r) => d.Score = (int)r, _ => { }, maxAttempts: 2, cancellationToken: cancellationToken);
            throw new Exception("Write failure was swallowed.");
        }
        catch (IOException) when (uncertain) { }
        catch (CosmosException ex) when (!uncertain && ex.StatusCode == HttpStatusCode.PreconditionFailed) { }
        Check(writes == (uncertain ? 1 : 2), "Unexpected retries after write failure.");
    }

    [Test]
    public async Task Missing_and_out_of_scope_documents_do_not_invoke_the_mutation(CancellationToken cancellationToken)
    {
        await using var fixture = new CosmosRepositoryAdapterFixture();
        fixture.Store.Setup(c => c.ReadItemStreamAsync("missing", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(HttpStatusCode.NotFound));
        fixture.Store.Setup(c => c.ReadItemStreamAsync("other", new PartitionKey("org"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Response(HttpStatusCode.OK, "{\"id\":\"other\",\"partitionKey\":\"org\",\"name\":\"other\"}", "version"));
        var repository = fixture.Resolve<ScopedCosmosAdapterRepository>();
        foreach (string id in new[] { "org:missing", "org:other" })
        {
            NativeDocument? result = await repository.MutateItemAtRevision(id, 0, d => d.Score,
                (d, r) => d.Score = (int)r, _ => throw new Exception("Inaccessible document mutated."), cancellationToken: cancellationToken);
            Check(result is null, "Inaccessible document returned.");
        }
    }

    private static ResponseMessage Response(HttpStatusCode status, string? body = null, string? etag = null)
    {
        var response = new ResponseMessage(status);
        if (body is not null) response.Content = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (etag is not null) response.Headers.Add("etag", etag);
        return response;
    }
}
