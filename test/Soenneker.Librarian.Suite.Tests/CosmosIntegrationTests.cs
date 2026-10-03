using System.Threading.Tasks;
using System.Linq;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.Cosmos;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CosmosIntegrationTests
{
    [Test]
    public async Task Cosmos_native_contract()
    {
        await using CosmosPersistenceFixture fixture = await CosmosPersistenceFixture.Create();
        ILibrarianContainer items = await fixture.Database.GetContainer("items");
        await items.AddItem("B", "{\"score_value\":2,\"name\":\"beta\"}");
        await items.AddItem("a", "{\"score_value\":1,\"name\":\"alpha\"}");
        DocumentProviderAssertions.Check(await items.GetItem("A") == "{\"score_value\":1,\"name\":\"alpha\"}", "Raw read changed.");
        DocumentProviderAssertions.Check((await items.GetItems(["A", "missing", "a"]))[1] is null, "Bulk read missing ID changed.");
        IQueryable<QueryableRow> query = items.BuildQueryable<QueryableRow>();
        DocumentProviderAssertions.Check(await query.Where(x => x.Score >= 1).CountAsync() == 2, "Native count failed.");
        DocumentProviderAssertions.Check(await query.LongCountAsync() == 2 && await query.AnyAsync(x => x.Score == 2) &&
            await query.AllAsync(x => x.Score > 0), "Native async terminals failed.");
        DocumentProviderAssertions.Check(await query.ExecuteAsync(q => q.Sum(x => x.Score)) == 3, "Native aggregate failed.");
        DocumentProviderAssertions.Check((await query.Select(x => x.Name).ToListAsync()).Count == 2, "Unbounded native projection failed.");
        DocumentProviderAssertions.Check((await query.Where(x => x.Name!.Contains("al")).ToListAsync()).Single().Score == 1, "Native filter failed.");
        DocumentProviderAssertions.Check(await query.OrderByDescending(x => x.Score).Select(x => x.Name).FirstAsync() == "beta", "Native projection failed.");
        await items.EnsureIndex("score_value");
        DocumentProviderAssertions.Check((await items.FindRangeByIndex<QueryableRow>("score_value", 1, 2, true, 0, 1)).Items[0].Score == 2, "Native range index failed.");
        await fixture.Database.UnloadContainer("items");
        items = await fixture.Database.GetContainer("items");
        DocumentProviderAssertions.Check(await items.CountByIndex("score_value", 2) == 1, "Native index not persisted.");
        await items.DeleteAllItems();
        DocumentProviderAssertions.Check(await items.CountItems() == 0, "Atomic clear failed.");
    }

    [Test]
    public async Task Cosmos_conditions_on_other_documents_prevent_write_skew()
    {
        await using CosmosPersistenceFixture fixture = await CosmosPersistenceFixture.Create();
        await using CosmosLibrarianDatabase second = fixture.CreateDatabase();
        ILibrarianContainer items = await fixture.Database.GetContainer("items");
        for (int i = 0; i < 5; i++)
        {
            string a = "a" + i, b = "b" + i;
            await items.AddItem(a, "0");
            await items.AddItem(b, "0");
            bool[] results = await Task.WhenAll(
                fixture.Database.Execute(new LibrarianBatch([new LibrarianWrite("items", a, "1")], [new LibrarianCondition("items", b, "0")])).AsTask(),
                second.Execute(new LibrarianBatch([new LibrarianWrite("items", b, "1")], [new LibrarianCondition("items", a, "0")])).AsTask());
            DocumentProviderAssertions.Check(results[0] != results[1], "Read-only conditions permitted write skew.");
        }
        DocumentProviderAssertions.Check(await fixture.Database.Execute(new LibrarianBatch([new LibrarianWrite("items", "new", "created")],
            [new LibrarianCondition("items", "absent", null)])), "Missing condition failed.");
        DocumentProviderAssertions.Check(await items.GetItem("absent") is null, "Absent condition left a placeholder behind.");
    }

    [Test]
    public async Task Cosmos_instances_coordinate_conditional_batches()
    {
        await using CosmosPersistenceFixture fixture = await CosmosPersistenceFixture.Create();
        await using CosmosLibrarianDatabase second = fixture.CreateDatabase();
        ILibrarianContainer items = await fixture.Database.GetContainer("items");
        await items.AddItem("a", "before");
        bool[] results = await Task.WhenAll(
            fixture.Database.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", "first"), new LibrarianWrite("other", "b", "first")], [new LibrarianCondition("items", "a", "before")])).AsTask(),
            second.Execute(new LibrarianBatch([new LibrarianWrite("items", "a", "second"), new LibrarianWrite("other", "b", "second")], [new LibrarianCondition("items", "a", "before")])).AsTask());
        DocumentProviderAssertions.Check(results[0] != results[1], "Competing conditions were not atomic.");
        DocumentProviderAssertions.Check(await items.GetItem("a") == await (await second.GetContainer("other")).GetItem("b"), "Partial batch publication.");
    }
}
