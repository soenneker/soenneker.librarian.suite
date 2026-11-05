using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Core;
using Soenneker.Librarian.Memory.Registrars;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class RevisionMutationTests
{
    private static ServiceProvider Services() => new ServiceCollection().AddLogging()
        .AddMemoryLibrarianDatabaseAsSingleton()
        .AddSingleton<ILibrarianRepository<RevisionDocument>>(p => new LibrarianRepository<RevisionDocument>(
            new ConfigurationBuilder().Build(), p.GetRequiredService<ILogger<LibrarianRepository<RevisionDocument>>>(),
            p.GetRequiredService<ILibrarianDatabase>(), "revisions")).BuildServiceProvider();

    [Test]
    public async Task Stale_and_duplicate_saves_do_not_invoke_mutations(CancellationToken cancellationToken)
    {
        await using ServiceProvider services = Services();
        var repository = services.GetRequiredService<ILibrarianRepository<RevisionDocument>>();
        await repository.AddItem(new RevisionDocument { Id = "one", Name = "initial" }, cancellationToken);
        await repository.MutateItemAtRevision("one", 0, d => d.Revision, (d, r) => d.Revision = r,
            d => d.Name = "new", cancellationToken: cancellationToken);
        try
        {
            await repository.MutateItemAtRevision("one", 0, d => d.Revision, (d, r) => d.Revision = r,
                _ => throw new Exception("Stale mutation executed."), cancellationToken: cancellationToken);
            throw new Exception("Expected revision conflict.");
        }
        catch (LibrarianRevisionConflictException ex)
        {
            Check(ex.ExpectedRevision == 0 && ex.ActualRevision == 1, "Incorrect conflict revisions.");
        }
        RevisionDocument current = (await repository.GetItem("one", cancellationToken))!;
        Check(current.Revision == 1 && current.Name == "new", "Rejected save changed the document.");
    }

    [Test]
    public async Task Revision_is_rechecked_after_a_competing_write(CancellationToken cancellationToken)
    {
        await using ServiceProvider services = Services();
        var repository = services.GetRequiredService<ILibrarianRepository<RevisionDocument>>();
        await repository.AddItem(new RevisionDocument { Id = "one" }, cancellationToken);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        Task older = Task.Run(async () =>
        {
            try
            {
                await repository.MutateItemAtRevision("one", 0, d => d.Revision, (d, r) => d.Revision = r, d =>
                {
                    Interlocked.Increment(ref calls);
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10), cancellationToken)) throw new TimeoutException();
                    d.Name = "older";
                }, cancellationToken: cancellationToken);
                throw new Exception("Competing edit was overwritten.");
            }
            catch (LibrarianRevisionConflictException) { }
        }, cancellationToken);
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(10), cancellationToken), "Mutation did not start.");
            await repository.MutateItemAtRevision("one", 0, d => d.Revision, (d, r) => d.Revision = r,
                d => d.Name = "winner", cancellationToken: cancellationToken);
        }
        finally { release.Set(); }
        await older;
        Check(calls == 1 && (await repository.GetItem("one", cancellationToken))!.Name == "winner", "Stale retry ran.");
    }

    [Test]
    public async Task Cancellation_and_invalid_callbacks_leave_the_document_unchanged(CancellationToken cancellationToken)
    {
        await using ServiceProvider services = Services();
        var repository = services.GetRequiredService<ILibrarianRepository<RevisionDocument>>();
        await repository.AddItem(new RevisionDocument { Id = "one", Name = "original" }, cancellationToken);
        try
        {
            await repository.MutateItemAtRevision("one", 0, d => d.Revision, (d, r) => d.Revision = r,
                _ => throw new Exception("Cancelled mutation executed."), cancellationToken: new CancellationToken(true));
            throw new Exception("Cancellation ignored.");
        }
        catch (OperationCanceledException) { }
        try
        {
            await repository.MutateItemAtRevision("one", 0, d => d.Revision, (d, r) => d.Revision = r,
                d => d.Id = "other", cancellationToken: cancellationToken);
            throw new Exception("Changed identity accepted.");
        }
        catch (ArgumentException) { }
        try
        {
            await repository.MutateItemAtRevision("one", 0, d => d.Revision, (_, _) => { },
                d => d.Name = "unsaved", cancellationToken: cancellationToken);
            throw new Exception("Broken revision setter accepted.");
        }
        catch (ArgumentException) { }
        RevisionDocument current = (await repository.GetItem("one", cancellationToken))!;
        Check(current.Revision == 0 && current.Name == "original", "Invalid mutation was committed.");
    }

    [Test]
    public async Task Sections_are_independent_and_invalid_revisions_cannot_write(CancellationToken cancellationToken)
    {
        await using ServiceProvider services = Services();
        var repository = services.GetRequiredService<ILibrarianRepository<RevisionDocument>>();
        await repository.AddItem(new RevisionDocument { Id = "one" }, cancellationToken);
        await repository.MutateItemAtRevision("one", 0, d => d.Revision, (d, r) => d.Revision = r,
            d => d.Name = "kept", cancellationToken: cancellationToken);
        await repository.MutateItemAtRevision("one", 0, d => d.OtherRevision, (d, r) => d.OtherRevision = r,
            _ => { }, cancellationToken: cancellationToken);
        RevisionDocument current = (await repository.GetItem("one", cancellationToken))!;
        Check(current.Revision == 1 && current.OtherRevision == 1 && current.Name == "kept", "Sections interfered.");
        try
        {
            await repository.MutateItemAtRevision("one", -1, d => d.Revision, (d, r) => d.Revision = r,
                _ => throw new Exception("Invalid mutation executed."), cancellationToken: cancellationToken);
            throw new Exception("Negative revision accepted.");
        }
        catch (ArgumentOutOfRangeException) { }
        try
        {
            await repository.MutateItemAtRevision("one", long.MaxValue, d => d.Revision, (d, r) => d.Revision = r,
                _ => throw new Exception("Overflow mutation executed."), cancellationToken: cancellationToken);
            throw new Exception("Overflow revision accepted.");
        }
        catch (OverflowException) { }
    }
}
