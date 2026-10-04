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

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CosmosMigrationTests
{
    [Test]
    public async Task Partition_scopes_batches_and_cross_partition_queries_remain_isolated()
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        ILibrarianDatabase database = fixture.Database;
        ILibrarianContainer a = await database.GetContainer("items", "org-a");
        ILibrarianContainer b = await database.GetContainer("items", "org-b");
        ILibrarianContainer root = await database.GetContainer("items");
        await a.AddItem("same", NativeDocumentJson.Create("same", "{\"score_value\":1}", "org-a"));
        await b.AddItem("same", NativeDocumentJson.Create("same", "{\"score_value\":2}", "org-b"));
        await root.AddItem("same", NativeDocumentJson.Create("same", "{\"score_value\":3}"));
        await using CosmosLibrarianDatabase otherDatabase = fixture.CreateDatabase("different-key");
        await (await otherDatabase.GetContainer("items", "org-a")).AddItem("same", NativeDocumentJson.Create("same", "{\"score_value\":999}", "org-a"));
        Check(await a.CountItems() == 1 && await b.CountItems() == 1, "Partition reads leaked.");
        Check((await a.GetItems(["same", "missing"]))[0] == NativeDocumentJson.Create("same", "{\"score_value\":1}", "org-a"), "Partition bulk read failed.");
        Check(await (await database.BuildQueryableAcrossPartitions<NativeDocument>("items")).CountAsync() == 3, "Cross-partition read missed documents.");
        Check(await database.Execute(new LibrarianBatch([new LibrarianWrite("items", "same", NativeDocumentJson.Create("same", "{\"score_value\":4}", "org-a"), (await a.GetItemWithVersion("same"))!.Version),
            new LibrarianWrite("items", "x", NativeDocumentJson.Create("x", "{}", "org-a"), CreateOnly: true)]), "org-a"), "Scoped batch failed.");
        Check(await b.GetItem("same") == NativeDocumentJson.Create("same", "{\"score_value\":2}", "org-b") && await root.GetItem("same") == NativeDocumentJson.Create("same", "{\"score_value\":3}"), "Batch escaped its partition.");
        Check(await a.GetItem("x") is not null, "Scoped batch failed.");
        await a.DeleteAllItems();
        Check(await a.CountItems() == 0 && await b.CountItems() == 1, "Clear escaped its partition.");
        await database.UnloadContainer("items");
        try { await b.GetItem("same"); throw new Exception("Scoped handle survived unload."); }
        catch (ObjectDisposedException) { }
        Check(await (await database.GetContainer("items", "org-b")).CountItems() == 1, "Unload deleted data.");
    }

    [Test]
    public async Task Native_pages_resume_and_reject_tokens_from_other_queries_or_partitions()
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", "org-a");
        for (var i = 0; i < 7; i++) await items.AddItem(i.ToString(), NativeDocumentJson.Create(i.ToString(), $"{{\"score_value\":{i}}}", "org-a"));
        IQueryable<int> query = items.BuildQueryable<NativeDocument>().OrderBy(row => row.Score).Select(row => row.Score);
        LibrarianPage<int> first = await query.ToPageAsync(2);
        Check(first.ContinuationToken is not null && first.Items.Count <= 2, "First native page was not bounded.");
        var results = new List<int>(first.Items);
        string? token = first.ContinuationToken;
        for (var page = 0; token is not null && page < 20; page++)
        {
            LibrarianPage<int> next = await query.ToPageAsync(2, token);
            results.AddRange(next.Items);
            token = next.ContinuationToken;
        }
        Check(token is null && results.SequenceEqual(Enumerable.Range(0, 7)), "Continuation skipped or duplicated rows.");
        try { await query.Where(score => score > 2).ToPageAsync(2, first.ContinuationToken); throw new Exception("Changed query accepted token."); }
        catch (ArgumentException) { }
        IQueryable<int> other = (await fixture.Database.GetContainer("items", "org-b")).BuildQueryable<NativeDocument>().OrderBy(row => row.Score).Select(row => row.Score);
        try { await other.ToPageAsync(2, first.ContinuationToken); throw new Exception("Changed partition accepted token."); }
        catch (ArgumentException) { }
        await using CosmosLibrarianDatabase second = fixture.CreateDatabase();
        IQueryable<int> resumed = (await second.GetContainer("items", "org-a")).BuildQueryable<NativeDocument>().OrderBy(row => row.Score).Select(row => row.Score);
        Check((await resumed.ToPageAsync(2, first.ContinuationToken)).Items.SequenceEqual(results.Skip(first.Items.Count).Take(2)), "Another instance could not resume the token.");
        IQueryable<NativeDocument> cross = await fixture.Database.BuildQueryableAcrossPartitions<NativeDocument>("items");
        Check((await cross.OrderBy(row => row.Score).ToPageAsync(2)).Items.Count <= 2, "Cross-partition page failed.");
    }

    [Test]
    public async Task Versions_and_retrying_mutations_coordinate_across_instances()
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        await using CosmosLibrarianDatabase second = fixture.CreateDatabase();
        await VersionedProviderContract.Exercise(await fixture.Database.GetContainer("items", "org-a"), await second.GetContainer("items", "org-a"));
    }

    [Test]
    public async Task Typed_repository_preserves_partition_scope_and_document_identity()
    {
        await using var fixture = await CosmosPersistenceFixture.Create();
        var repository = new LibrarianRepository<ExampleDocument>(new ConfigurationBuilder().Build(),
            NullLogger<LibrarianRepository<ExampleDocument>>.Instance, fixture.Database, "typed", "org-a");
        await repository.AddItem(new ExampleDocument { Id = "org-a:one", Name = "before" });
        LibrarianItem<ExampleDocument> before = (await repository.GetItemWithVersion("one"))!;
        await repository.MutateItem("one", document => { document.Name = "after"; return document; });
        Check(await repository.UpdateItemIfVersion(before.Document, before.Version) is null, "Repository accepted a stale version.");
        Check((await repository.GetItem("one"))!.Name == "after", "Repository mutation not persisted.");
        try { await repository.MutateItem("one", document => { document.Id = "different"; return document; }); throw new Exception("ID mutation accepted."); }
        catch (ArgumentException) { }
        Check(await (await fixture.Database.GetContainer("typed", "org-b")).CountItems() == 0, "Repository escaped its partition.");
        LibrarianPage<ExampleDocument> page = await repository.GetItemsPaged(await repository.BuildQueryable<ExampleDocument>(), 1);
        Check(page.Items.Count == 1 && page.Items[0].Name == "after", "Repository paging failed.");
        LibrarianItem<ExampleDocument> current = (await repository.GetItemWithVersion("one"))!;
        Check(await repository.DeleteItemIfVersion("one", current.Version), "Repository conditional delete failed.");
    }
}
