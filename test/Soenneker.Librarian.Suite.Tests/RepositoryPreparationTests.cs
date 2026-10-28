using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Core;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Librarian.Memory.Registrars;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class RepositoryPreparationTests
{
    private static ServiceProvider Services(int concurrency = 1) => new ServiceCollection().AddLogging()
        .AddMemoryLibrarianDatabaseAsSingleton().AddSingleton<RecordingRepositoryLifecycle>()
        .AddSingleton<ILibrarianRepository<ExampleDocument>>(provider => new LibrarianRepository<ExampleDocument>(
            new ConfigurationBuilder().Build(), provider.GetRequiredService<ILogger<LibrarianRepository<ExampleDocument>>>(),
            provider.GetRequiredService<ILibrarianDatabase>(), "shared", null, new OwnedDocumentScope(),
            provider.GetRequiredService<RecordingRepositoryLifecycle>(), concurrency)).BuildServiceProvider();

    [Test]
    public async Task Scope_covers_queries_indexes_bulk_reads_and_destructive_operations(CancellationToken cancellationToken)
    {
        await using ServiceProvider services = Services();
        var repository = services.GetRequiredService<ILibrarianRepository<ExampleDocument>>();
        ILibrarianContainer container = await services.GetRequiredService<ILibrarianDatabase>().GetContainer("shared", cancellationToken);
        await container.AddItem("other", LibrarianJson.Serialize(new ExampleDocument { Id = "other", Name = "unrelated" }), cancellationToken);
        await repository.AddItems([new() { Id = "one", Name = "owned" }, new() { Id = "two", Name = "owned" }], cancellationToken);
        Check(await repository.GetItem("other", cancellationToken) is null, "Point read escaped scope.");
        Check(await repository.GetItemWithVersion("other", cancellationToken) is null, "Version read escaped scope.");
        ExampleDocument?[] rows = await repository.GetItems(new[] { "one", "other", "missing", "one" }, cancellationToken);
        Check(rows.Length == 4 && rows[0]?.Id == "one" && rows[1] is null && rows[2] is null && rows[3]?.Id == "one", "Bulk ordering or isolation failed.");
        IQueryable<ExampleDocument> query = await repository.BuildQueryable<ExampleDocument>(cancellationToken);
        Check(await query.Count(cancellationToken) == 2 && (await repository.GetAll(cancellationToken))!.Count == 2, "Query escaped scope.");
        await repository.EnsureIndex("name", cancellationToken);
        Check(await repository.CountByIndex("name", "unrelated", cancellationToken) == 0, "Index count escaped scope.");
        Check(!await repository.ExistsByIndex("name", "unrelated", cancellationToken), "Index existence escaped scope.");
        Check((await repository.FindByIndex("name", "owned", 1, 1, cancellationToken)).Items.Count == 1, "Scoped index paging failed.");
        Check((await repository.FindRangeByIndex("name", cancellationToken: cancellationToken)).Items.Count == 2, "Range escaped scope.");
        try { await repository.UpdateItem(new ExampleDocument { Id = "other", Name = "owned" }, cancellationToken); throw new Exception("Replaced unrelated type."); }
        catch (KeyNotFoundException) { }
        await repository.DeleteItem("other", cancellationToken);
        await repository.DeleteAll(cancellationToken);
        Check(await container.CountItems(cancellationToken) == 1 && await container.GetItem("other", cancellationToken) is not null, "Delete escaped scope.");
    }

    [Test]
    public async Task Lifecycle_covers_bulk_conditional_mutation_and_delete_without_observing_conflicts(CancellationToken cancellationToken)
    {
        await using ServiceProvider services = Services(2);
        var repository = services.GetRequiredService<ILibrarianRepository<ExampleDocument>>();
        var lifecycle = services.GetRequiredService<RecordingRepositoryLifecycle>();
        var documents = new List<ExampleDocument> { new() { Id = "one", Name = "owned" }, new() { Id = "two", Name = "owned" } };
        await repository.AddItems(documents, cancellationToken);
        LibrarianItem<ExampleDocument> old = (await repository.GetItemWithVersion("one", cancellationToken))!;
        await repository.UpdateItems(documents, cancellationToken);
        Check(await repository.UpdateItemIfVersion(old.Document, old.Version, cancellationToken) is null, "Stale version accepted.");
        await repository.MutateItem("one", document => document, cancellationToken: cancellationToken);
        Check(!await repository.DeleteItemIfVersion("one", old.Version, cancellationToken), "Stale delete accepted.");
        await repository.DeleteAll(cancellationToken);
        Check(lifecycle.Events.Count(value => value == "after-add") == 2 && lifecycle.Events.Count(value => value == "after-update") == 3 &&
            lifecycle.Events.Count(value => value == "after-delete") == 2, "Hooks missed a path or observed a failed write.");
        lifecycle.ChangeIdentity = true;
        try { await repository.AddItem(new ExampleDocument { Id = "three", Name = "owned" }, cancellationToken); throw new Exception("Hook changed identity."); }
        catch (ArgumentException) { }
        Check(await repository.GetItem("changed", cancellationToken) is null, "Invalid hook committed.");
    }

    [Test]
    public async Task Bulk_concurrency_is_bounded_and_post_commit_failures_are_not_retried(CancellationToken cancellationToken)
    {
        await using ServiceProvider services = Services(2);
        var repository = services.GetRequiredService<ILibrarianRepository<ExampleDocument>>();
        var lifecycle = services.GetRequiredService<RecordingRepositoryLifecycle>();
        lifecycle.DelayAdds = true;
        await repository.AddItems(Enumerable.Range(0, 12).Select(index => new ExampleDocument { Id = index.ToString(), Name = "owned" }).ToList(), cancellationToken);
        Check(lifecycle.PeakAdds == 2 && lifecycle.ActiveAdds == 0, "Bulk concurrency was unbounded or sequential.");
        LibrarianItem<ExampleDocument> before = (await repository.GetItemWithVersion("0", cancellationToken))!;
        lifecycle.ThrowAfterUpdate = true;
        int attempts = 0;
        try
        {
            await repository.MutateItem("0", document => { attempts++; return document; }, cancellationToken: cancellationToken);
            throw new Exception("After-hook error swallowed.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "After hook failed after commit.") { }
        Check(attempts == 1 && (await repository.GetItemWithVersion("0", cancellationToken))!.Version != before.Version,
            "An after-commit failure retried the write or rolled it back.");
        try { await repository.MutateItem("0", document => { document.Name = "unrelated"; return document; }, cancellationToken: cancellationToken); throw new Exception("Mutation escaped scope."); }
        catch (ArgumentException) { }
        Check((await repository.GetItem("0", cancellationToken))!.Name == "owned", "Out-of-scope mutation committed.");
    }
}
