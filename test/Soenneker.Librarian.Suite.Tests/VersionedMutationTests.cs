using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Soenneker.Librarian.Abstractions;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class VersionedMutationTests
{
    [Test]
    public async Task Uncertain_write_failures_are_not_retried()
    {
        var container = new Mock<ILibrarianContainer>(MockBehavior.Strict);
        container.Setup(c => c.GetItemWithVersion("one", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LibrarianItem<string>("{\"score_value\":1}", "version"));
        container.Setup(c => c.UpdateItemIfVersion("one", It.IsAny<string>(), "version", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Response lost after dispatch."));
        var mutations = 0;
        try
        {
            await container.Object.MutateItem<QueryableRow>("one", row => { mutations++; row.Score++; return row; });
            throw new Exception("Uncertain outcome was swallowed.");
        }
        catch (IOException) { }
        Check(mutations == 1, "Non-idempotent callback was retried after an uncertain outcome.");
        container.Verify(c => c.UpdateItemIfVersion("one", It.IsAny<string>(), "version", It.IsAny<CancellationToken>()), Times.Once);
    }
}
