using System;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Mongo;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class MongoProviderTests
{
    [Test]
    public async Task Versions_and_retrying_mutations_coordinate_across_instances(CancellationToken cancellationToken)
    {
        await using var fixture = await MongoPersistenceFixture.Create();
        await using MongoLibrarianDatabase second = fixture.CreateDatabase();
        await VersionedProviderContract.Exercise(await fixture.Database.GetContainer("items", "org-a", cancellationToken: cancellationToken), await second.GetContainer("items", "org-a", cancellationToken: cancellationToken));
    }
    [Test]
    public async Task Documents_are_stored_directly_and_scopes_use_document_identity(CancellationToken cancellationToken)
    {
        await using var fixture = await MongoPersistenceFixture.Create();
        ILibrarianContainer a = await fixture.Database.GetContainer("items", "org-a", cancellationToken: cancellationToken);
        ILibrarianContainer b = await fixture.Database.GetContainer("items", "org-b", cancellationToken: cancellationToken);
        string json = NativeDocumentJson.Create("one", "{\"score_value\":1}", "org-a");
        await a.AddItem("one", json, cancellationToken: cancellationToken);
        await b.AddItem("one", NativeDocumentJson.Create("one", "{\"score_value\":2}", "org-b"), cancellationToken: cancellationToken);
        BsonDocument stored = await fixture.Store("items").Find(new BsonDocument { { "id", "one" }, { "partitionKey", "org-a" } }).SingleAsync(cancellationToken: cancellationToken);
        Check(stored["score_value"].ToInt32() == 1 && !stored.Contains("body") && !stored.Contains("json") && !stored.Contains("values"), "Document was wrapped.");
        Check(await a.GetItem("ONE", cancellationToken: cancellationToken) is null, "Native identity must be case-sensitive.");
        Check(await a.CountItems(cancellationToken: cancellationToken) == 1 && await b.CountItems(cancellationToken: cancellationToken) == 1, "Partition isolation failed.");
        ILibrarianContainer root = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        Check(NativeDocumentJson.Score((await root.GetItem("org-b:one", cancellationToken: cancellationToken))!) == 2, "Composite identity failed.");
        Check((await root.GetItems(["org-b:one", "missing", "org-a:one", "org-b:one"], cancellationToken: cancellationToken)).Select(x => x is null ? -1 : NativeDocumentJson.Score(x)).SequenceEqual([2, -1, 1, 2]), "Bulk order or partitions changed.");
        Check(await root.BuildQueryable<NativeDocument>().CountAsync(d => d.DocumentId == "one", cancellationToken: cancellationToken) == 2, "Document members were not translated.");
        Check(await (await fixture.Database.BuildQueryableAcrossPartitions<NativeDocument>("items", cancellationToken: cancellationToken)).CountAsync(cancellationToken: cancellationToken) == 2, "Cross-partition query failed.");
        await a.DeleteAllItems(cancellationToken: cancellationToken);
        Check(await root.CountItems(cancellationToken: cancellationToken) == 1, "Scoped deletion crossed partitions.");
        await fixture.Database.UnloadContainer("items", cancellationToken: cancellationToken);
        try { await b.CountItems(cancellationToken: cancellationToken); throw new Exception("Unloaded scope survived."); } catch (ObjectDisposedException) { }
    }
    [Test]
    public async Task Native_indexes_query_top_level_fields_and_distinguish_missing_from_null(CancellationToken cancellationToken)
    {
        await using var fixture = await MongoPersistenceFixture.Create();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await items.AddItem("a", NativeDocumentJson.Create("a", "{\"score_value\":1,\"name\":null}"), cancellationToken: cancellationToken);
        await items.AddItem("b", NativeDocumentJson.Create("b", "{\"score_value\":2}"), cancellationToken: cancellationToken);
        await items.EnsureIndex("score_value", cancellationToken: cancellationToken);
        Check(await items.CountRangeByIndex("score_value", 1, 2, cancellationToken: cancellationToken) == 2, "Native range count failed.");
        Check((await items.FindRangeByIndex<NativeDocument>("score_value", 1, 2, true, take: 1, cancellationToken: cancellationToken)).Items[0].Score == 2, "Native ordering failed.");
        Check(await items.CountByIndex("name", null, cancellationToken: cancellationToken) == 1, "Missing and null were merged.");
        await items.AddItem("c", "{\"id\":\"c\",\"partitionKey\":\"c\",\"nested\":{\"value\":1},\"nested\":2}", cancellationToken: cancellationToken);
        Check(await items.CountByIndex("nested.value", 1, cancellationToken: cancellationToken) == 0, "Overwritten nested property remained indexed.");
    }
    [Test]
    public async Task Native_batches_coordinate_across_instances_and_collections(CancellationToken cancellationToken)
    {
        await using var fixture = await MongoPersistenceFixture.Create();
        await using MongoLibrarianDatabase second = fixture.CreateDatabase();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        string before = NativeDocumentJson.Create("a", "{\"score_value\":0}");
        await items.AddItem("a", before, cancellationToken: cancellationToken);
        string version = (await items.GetItemWithVersion("a", cancellationToken: cancellationToken))!.Version;
        LibrarianBatch Batch(int score) => new([new LibrarianWrite("items", "a", NativeDocumentJson.Create("a", $"{{\"score_value\":{score}}}"), version),
            new LibrarianWrite("other", "a", NativeDocumentJson.Create("a", $"{{\"score_value\":{score}}}"), CreateOnly: true)]);
        bool[] outcomes = await Task.WhenAll(fixture.Database.Execute(Batch(1), cancellationToken: cancellationToken).AsTask(), second.Execute(Batch(2), cancellationToken: cancellationToken).AsTask());
        Check(outcomes[0] != outcomes[1], "Competing batches did not serialize.");
        Check(await items.GetItem("a", cancellationToken: cancellationToken) == await (await second.GetContainer("other", cancellationToken: cancellationToken)).GetItem("a", cancellationToken: cancellationToken), "Partial batch committed.");
        await NativeBatchContract.Exercise(fixture.Database);
        Check(await items.GetItem("absent", cancellationToken: cancellationToken) is null, "Guard placeholder persisted.");
    }
    [Test]
    public async Task Mismatched_identity_is_rejected_before_writing(CancellationToken cancellationToken)
    {
        await using var fixture = await MongoPersistenceFixture.Create();
        ILibrarianContainer items = await fixture.Database.GetContainer("items", "org-a", cancellationToken: cancellationToken);
        try { await items.AddItem("one", NativeDocumentJson.Create("one"), cancellationToken: cancellationToken); throw new Exception("Mismatched partition accepted."); } catch (ArgumentException) { }
        try { await items.AddItem("one", "{}", cancellationToken: cancellationToken); throw new Exception("Missing Document fields accepted."); } catch (ArgumentException) { }
        Check(await items.CountItems(cancellationToken: cancellationToken) == 0, "Invalid document persisted.");
    }
}
