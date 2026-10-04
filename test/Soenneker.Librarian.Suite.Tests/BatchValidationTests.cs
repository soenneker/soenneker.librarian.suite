using System;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions.Transactions;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class BatchValidationTests
{
    [Test]
    public async Task Snapshot_providers_reject_native_concurrency_options_before_writing()
    {
        foreach (string provider in new[] { "memory", "filesystem" })
        {
            await using var fixture = new BatchFixture(provider);
            foreach (LibrarianWrite write in new[] { new LibrarianWrite("items", "a", "value", "version"), new LibrarianWrite("items", "a", "value", CreateOnly: true) })
            {
                try { await fixture.Database.Execute(new LibrarianBatch([write])); throw new Exception("Unsupported concurrency silently ignored."); }
                catch (NotSupportedException) { }
            }
            Check(await (await fixture.Database.GetContainer("items")).CountItems() == 0, "Rejected batch wrote data.");
        }
    }
    [Test]
    public void Batch_validation_rejects_duplicates_and_conflicting_options()
    {
        foreach (LibrarianWrite[] writes in new[] {
            new[] { new LibrarianWrite("items", "a", "1"), new LibrarianWrite("items", "A", "2") },
            new[] { new LibrarianWrite("items", "a", null, CreateOnly: true) },
            new[] { new LibrarianWrite("items", "a", "1", "version", CreateOnly: true) },
            new[] { new LibrarianWrite("items", "a", "1", " ") } })
        {
            try { _ = new LibrarianBatch(writes); throw new Exception("Invalid batch accepted."); } catch (ArgumentException) { }
        }
    }
}
