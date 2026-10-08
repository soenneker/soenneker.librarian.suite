using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Core;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Cosmos;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CosmosMigrationTests
{
    [Test]
    public async Task Partition_scopes_batches_and_cross_partition_queries_remain_isolated(CancellationToken cancellationToken)
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        ILibrarianDatabase database = fixture.Database;
        ILibrarianContainer a = await database.GetContainer("items", "org-a", cancellationToken: cancellationToken);
        ILibrarianContainer b = await database.GetContainer("items", "org-b", cancellationToken: cancellationToken);
        ILibrarianContainer root = await database.GetContainer("items", cancellationToken: cancellationToken);
        await a.AddItem("same", NativeDocumentJson.Create("same", "{\"score_value\":1}", "org-a"), cancellationToken: cancellationToken);
        await b.AddItem("same", NativeDocumentJson.Create("same", "{\"score_value\":2}", "org-b"), cancellationToken: cancellationToken);
        await root.AddItem("same", NativeDocumentJson.Create("same", "{\"score_value\":3}"), cancellationToken: cancellationToken);
        await using CosmosLibrarianDatabase otherDatabase = fixture.CreateDatabase("different-key");
        await (await otherDatabase.GetContainer("items", "org-a", cancellationToken: cancellationToken)).AddItem("same", NativeDocumentJson.Create("same", "{\"score_value\":999}", "org-a"), cancellationToken: cancellationToken);
        Check(await a.CountItems(cancellationToken: cancellationToken) == 1 && await b.CountItems(cancellationToken: cancellationToken) == 1, "Partition reads leaked.");
        Check((await a.GetItems(["same", "missing"], cancellationToken: cancellationToken))[0] == NativeDocumentJson.Create("same", "{\"score_value\":1}", "org-a"), "Partition bulk read failed.");
        Check(await (await database.BuildQueryableAcrossPartitions<NativeDocument>("items", cancellationToken: cancellationToken)).CountAsync(cancellationToken: cancellationToken) == 3, "Cross-partition read missed documents.");
        Check(await database.Execute(new LibrarianBatch([new LibrarianWrite("items", "same", NativeDocumentJson.Create("same", "{\"score_value\":4}", "org-a"), (await a.GetItemWithVersion("same", cancellationToken: cancellationToken))!.Version),
            new LibrarianWrite("items", "x", NativeDocumentJson.Create("x", "{}", "org-a"), CreateOnly: true)]), "org-a", cancellationToken: cancellationToken), "Scoped batch failed.");
        Check(await b.GetItem("same", cancellationToken: cancellationToken) == NativeDocumentJson.Create("same", "{\"score_value\":2}", "org-b") && await root.GetItem("same", cancellationToken: cancellationToken) == NativeDocumentJson.Create("same", "{\"score_value\":3}"), "Batch escaped its partition.");
        Check(await a.GetItem("x", cancellationToken: cancellationToken) is not null, "Scoped batch failed.");
        await a.DeleteAllItems(cancellationToken: cancellationToken);
        Check(await a.CountItems(cancellationToken: cancellationToken) == 0 && await b.CountItems(cancellationToken: cancellationToken) == 1, "Clear escaped its partition.");
        await database.UnloadContainer("items", cancellationToken: cancellationToken);
        try { await b.GetItem("same", cancellationToken: cancellationToken); throw new Exception("Scoped handle survived unload."); }
        catch (ObjectDisposedException) { }
        Check(await (await database.GetContainer("items", "org-b", cancellationToken: cancellationToken)).CountItems(cancellationToken: cancellationToken) == 1, "Unload deleted data.");
    }

    [Test]
    public async Task Native_pages_resume_and_reject_tokens_from_other_queries_or_partitions(CancellationToken cancellationToken)
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", "org-a", cancellationToken: cancellationToken);
        for (var i = 0; i < 7; i++) await items.AddItem(i.ToString(), NativeDocumentJson.Create(i.ToString(), $"{{\"score_value\":{i}}}", "org-a"), cancellationToken: cancellationToken);
        IQueryable<int> query = items.BuildQueryable<NativeDocument>().OrderBy(row => row.Score).Select(row => row.Score);
        LibrarianPage<int> first = await query.ToPageAsync(2, cancellationToken: cancellationToken);
        Check(first.ContinuationToken is not null && first.Items.Count <= 2, "First native page was not bounded.");
        var results = new List<int>(first.Items);
        string? token = first.ContinuationToken;
        for (var page = 0; token is not null && page < 20; page++)
        {
            LibrarianPage<int> next = await query.ToPageAsync(2, token, cancellationToken: cancellationToken);
            results.AddRange(next.Items);
            token = next.ContinuationToken;
        }
        Check(token is null && results.SequenceEqual(Enumerable.Range(0, 7)), "Continuation skipped or duplicated rows.");
        try { await query.Where(score => score > 2).ToPageAsync(2, first.ContinuationToken, cancellationToken: cancellationToken); throw new Exception("Changed query accepted token."); }
        catch (ArgumentException) { }
        IQueryable<int> other = (await fixture.Database.GetContainer("items", "org-b", cancellationToken: cancellationToken)).BuildQueryable<NativeDocument>().OrderBy(row => row.Score).Select(row => row.Score);
        try { await other.ToPageAsync(2, first.ContinuationToken, cancellationToken: cancellationToken); throw new Exception("Changed partition accepted token."); }
        catch (ArgumentException) { }
        await using CosmosLibrarianDatabase second = fixture.CreateDatabase();
        IQueryable<int> resumed = (await second.GetContainer("items", "org-a", cancellationToken: cancellationToken)).BuildQueryable<NativeDocument>().OrderBy(row => row.Score).Select(row => row.Score);
        Check((await resumed.ToPageAsync(2, first.ContinuationToken, cancellationToken: cancellationToken)).Items.SequenceEqual(results.Skip(first.Items.Count).Take(2)), "Another instance could not resume the token.");
        IQueryable<NativeDocument> cross = await fixture.Database.BuildQueryableAcrossPartitions<NativeDocument>("items", cancellationToken: cancellationToken);
        Check((await cross.OrderBy(row => row.Score).ToPageAsync(2, cancellationToken: cancellationToken)).Items.Count <= 2, "Cross-partition page failed.");
    }

    [Test]
    public async Task Versions_and_retrying_mutations_coordinate_across_instances(CancellationToken cancellationToken)
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        await using CosmosLibrarianDatabase second = fixture.CreateDatabase();
        await VersionedProviderContract.Exercise(await fixture.Database.GetContainer("items", "org-a", cancellationToken: cancellationToken), await second.GetContainer("items", "org-a", cancellationToken: cancellationToken));
    }

    [Test]
    public async Task Typed_repository_preserves_partition_scope_and_document_identity(CancellationToken cancellationToken)
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        var repository = new LibrarianRepository<ExampleDocument>(new ConfigurationBuilder().Build(),
            NullLogger<LibrarianRepository<ExampleDocument>>.Instance, fixture.Database, "typed", "org-a");
        await repository.AddItem(new ExampleDocument { Id = "org-a:one", Name = "before" }, cancellationToken: cancellationToken);
        LibrarianItem<ExampleDocument> before = (await repository.GetItemWithVersion("one", cancellationToken: cancellationToken))!;
        await repository.MutateItem("one", document => { document.Name = "after"; return document; }, cancellationToken: cancellationToken);
        Check(await repository.UpdateItemIfVersion(before.Document, before.Version, cancellationToken: cancellationToken) is null, "Repository accepted a stale version.");
        Check((await repository.GetItem("one", cancellationToken: cancellationToken))!.Name == "after", "Repository mutation not persisted.");
        try { await repository.MutateItem("one", document => { document.Id = "different"; return document; }, cancellationToken: cancellationToken); throw new Exception("ID mutation accepted."); }
        catch (ArgumentException) { }
        Check(await (await fixture.Database.GetContainer("typed", "org-b", cancellationToken: cancellationToken)).CountItems(cancellationToken: cancellationToken) == 0, "Repository escaped its partition.");
        LibrarianPage<ExampleDocument> page = await repository.GetItemsPaged(await repository.BuildQueryable<ExampleDocument>(cancellationToken: cancellationToken), 1, cancellationToken: cancellationToken);
        Check(page.Items.Count == 1 && page.Items[0].Name == "after", "Repository paging failed.");
        LibrarianItem<ExampleDocument> current = (await repository.GetItemWithVersion("one", cancellationToken: cancellationToken))!;
        Check(await repository.DeleteItemIfVersion("one", current.Version, cancellationToken: cancellationToken), "Repository conditional delete failed.");
    }
}
