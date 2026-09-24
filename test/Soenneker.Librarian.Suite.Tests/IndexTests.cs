using Soenneker.Utils.MemoryStream;
using Soenneker.Utils.File.Abstract;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Core;
using Soenneker.Librarian.FileSystem.Registrars;
using Soenneker.Librarian.Memory;

namespace Soenneker.Librarian.Suite.Tests;

public class IndexTests
{
    private static readonly IFileUtil _fileUtil = new Soenneker.Utils.File.FileUtil(NullLogger<Soenneker.Utils.File.FileUtil>.Instance, new MemoryStreamUtil());

    [Test]
    public async Task Randomized_mutations_and_rank_paging_match_a_reference_model()
    {
        await using MemoryLibrarianDatabase database = CreateDatabase();
        ILibrarianContainer container = await database.GetContainer("random");
        await container.EnsureIndex("score");
        var expected = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var random = new Random(7919);
        for (var step = 0; step < 6000; step++)
        {
            var id = $"id-{random.Next(600):D4}";
            if (step % 997 == 996)
            {
                await container.DeleteAllItems();
                expected.Clear();
            }
            else if (random.Next(4) == 0 && expected.Remove(id))
                await container.DeleteItem(id.ToUpperInvariant());
            else
            {
                int score = random.Next(-30, 31);
                var json = $"{{\"id\":\"{id}\",\"score\":{score}}}";
                if (expected.ContainsKey(id)) await container.UpdateItemStrict(id.ToUpperInvariant(), json);
                else await container.AddItem(id, json);
                expected[id] = score;
            }

            if (step % 7 != 0) continue;
            int minimum = random.Next(-40, 20);
            int maximum = minimum + random.Next(30);
            int skip = random.Next(0, 120);
            int take = random.Next(1, 30);
            bool descending = random.Next(2) == 0;
            IOrderedEnumerable<KeyValuePair<string, int>> ordered = expected.Where(pair => pair.Value >= minimum && pair.Value <= maximum)
                                                                            .OrderBy(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase);
            string[] ids = (descending ? ordered.Reverse() : ordered).Skip(skip).Take(take).Select(pair => pair.Key).ToArray();
            LibrarianQueryResult<IndexRow> page = await container.FindRangeByIndex<IndexRow>("score", minimum, maximum, descending, skip, take);
            Check(page.Items.Select(row => row.Id).SequenceEqual(ids), $"Range mismatch at mutation {step}.");
            Check(page.IndexEntriesExamined == ids.Length, "Paging visited skipped entries.");
            int key = random.Next(-30, 31);
            string[] equal = expected.Where(pair => pair.Value == key).Select(pair => pair.Key)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Skip(skip).Take(take).ToArray();
            LibrarianQueryResult<IndexRow> matches = await container.FindByIndex<IndexRow>("score", key, skip, take);
            Check(matches.Items.Select(row => row.Id).SequenceEqual(equal), $"Equality mismatch at mutation {step}.");
            Check(await container.CountByIndex("score", key) == expected.Count(pair => pair.Value == key), "Cached count drifted.");
        }
    }

    [Test]
    public async Task Returned_pages_survive_pool_reuse_and_failed_deserialization()
    {
        await using MemoryLibrarianDatabase database = CreateDatabase();
        ILibrarianContainer container = await database.GetContainer("pooled");
        await container.EnsureIndex("score");
        await container.AddItem("one", "{\"id\":\"one\",\"score\":1}");
        LibrarianQueryResult<IndexRow> first = await container.FindByIndex<IndexRow>("score", 1);
        first.Items[0].Score = 500;
        await Throws<JsonException>(async () => await container.FindByIndex<int>("score", 1));
        await container.UpdateItemStrict("one", "{\"id\":\"one\",\"score\":2}");
        for (var i = 0; i < 100; i++)
        {
            LibrarianQueryResult<IndexRow> next = await container.FindByIndex<IndexRow>("score", 2);
            Check(next.Items[0].Score == 2, "A pooled buffer or returned object leaked into another query.");
        }
        Check(first.Items[0].Score == 500, "A later query changed a previously returned page.");
    }

    private static MemoryLibrarianDatabase CreateDatabase() => new(NullLogger<MemoryLibrarianDatabase>.Instance);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    [Test]
    public async Task Equality_materializes_only_the_requested_page_and_count_materializes_nothing()
    {
        await using MemoryLibrarianDatabase database = CreateDatabase();
        ILibrarianContainer container = await database.GetContainer("items");
        for (var i = 0; i < 2000; i++)
            await container.AddItem($"id-{i:D4}", i < 10
                ? "{\"status\":\"active\",\"date\":\"2026-01-01\"}"
                : "{\"status\":\"inactive\",\"date\":\"not-a-date\"}");
        CountedRow.Created = 0;
        await container.EnsureIndex("status");
        Check(CountedRow.Created == 0, "Index creation materialized documents.");
        Check(await container.CountByIndex("status", "active") == 10, "Count is incorrect.");
        Check(await container.ExistsByIndex("status", "active"), "Existence lookup failed.");
        Check(!await container.ExistsByIndex("status", "missing"), "Missing value matched.");
        Check(CountedRow.Created == 0, "Count or existence materialized documents.");
        LibrarianQueryResult<CountedRow> page = await container.FindByIndex<CountedRow>("status", "active", skip: 2, take: 3);
        Check(page.Items.Count == 3 && CountedRow.Created == 3 && page.DocumentsDeserialized == 3,
            "Query deserialized outside its result page.");
        Check(page.IndexEntriesExamined == 3 && page.Index == "status", "Unexpected query work.");
    }

    [Test]
    public async Task Equality_handles_null_missing_nested_properties_and_scalar_types()
    {
        await using MemoryLibrarianDatabase database = CreateDatabase();
        ILibrarianContainer container = await database.GetContainer("items");
        await container.EnsureIndex("profile.key");
        foreach ((string id, string json) in new[]
        {
            ("null", "{\"profile\":{\"key\":null}}"), ("missing", "{}"),
            ("number", "{\"profile\":{\"key\":1.00}}"), ("string", "{\"profile\":{\"key\":\"1\"}}"),
            ("upper", "{\"profile\":{\"key\":\"ACTIVE\"}}"), ("boolean", "{\"profile\":{\"key\":true}}")
        }) await container.AddItem(id, json);
        Check(await container.CountByIndex("profile.key", null) == 1, "Null matched a missing property.");
        Check(await container.CountByIndex("profile.key", 1) == 1, "Numeric normalization failed.");
        Check(await container.CountByIndex("profile.key", "1") == 1, "String matched numeric data.");
        Check(await container.CountByIndex("profile.key", true) == 1, "Boolean lookup failed.");
        Check(await container.CountByIndex("profile.key", "active") == 0, "String matching was not ordinal.");
    }

    [Test]
    public async Task Updates_deletes_and_clear_maintain_all_indexes()
    {
        await using MemoryLibrarianDatabase database = CreateDatabase();
        ILibrarianContainer container = await database.GetContainer("items");
        await container.EnsureIndex("status");
        await container.EnsureIndex("score");
        await container.AddItem("ONE", "{\"status\":\"old\",\"score\":1}");
        await container.UpdateItemStrict("one", "{\"status\":\"new\",\"score\":2}");
        Check(await container.CountByIndex("status", "old") == 0 && await container.CountByIndex("score", 1) == 0,
            "Update retained old entries.");
        Check(await container.CountByIndex("status", "new") == 1, "Update lost new entries.");
        Check((await container.FindRangeByIndex<IndexRow>("score", 1, 1)).Items.Count == 0,
            "Update retained the old ordered entry.");
        Check((await container.FindRangeByIndex<IndexRow>("score", 2, 2)).Items.Count == 1,
            "Update lost the new ordered entry.");
        await container.DeleteItem("oNe");
        Check(await container.CountByIndex("score", 2) == 0, "Delete retained entries.");
        await container.AddItem("two", "{\"score\":3}");
        await container.DeleteAllItems();
        Check(await container.CountByIndex("score", 3) == 0, "Clear retained entries.");
        await container.AddItem("three", "{\"score\":4}");
        Check(await container.CountByIndex("score", 4) == 1, "Clear removed the index definition.");
    }

    [Test]
    public async Task Invalid_indexed_writes_and_cancelled_mutations_leave_data_and_indexes_unchanged()
    {
        await using MemoryLibrarianDatabase database = CreateDatabase();
        ILibrarianContainer container = await database.GetContainer("items");
        await container.EnsureIndex("status");
        await container.EnsureIndex("score");
        const string original = "{\"status\":\"old\",\"score\":1}";
        await container.AddItem("one", original);
        await Throws<ArgumentException>(async () => await container.UpdateItem("one", "{\"status\":\"new\",\"score\":[]}"));
        await Throws<JsonException>(async () => await container.AddItem("two", "malformed"));
        await Throws<OperationCanceledException>(async () => await container.UpdateItem("one", "{}", new CancellationToken(true)));
        Check((await container.GetItem("one")) == original && (await container.GetItem("two")) is null, "Rejected write changed documents.");
        Check(await container.CountByIndex("status", "old") == 1 && await container.CountByIndex("status", "new") == 0,
            "Rejected write changed indexes.");
    }

    [Test]
    public async Task Index_creation_is_atomic_idempotent_and_never_implicitly_scans_queries()
    {
        await using MemoryLibrarianDatabase database = CreateDatabase();
        ILibrarianContainer container = await database.GetContainer("items");
        await container.AddItem("one", "{\"score\":1}");
        await container.AddItem("bad", "not-json");
        await Throws<JsonException>(async () => await container.EnsureIndex("score"));
        await Throws<InvalidOperationException>(async () => await container.CountByIndex("score", 1));
        await container.DeleteItem("bad");
        await Throws<OperationCanceledException>(async () => await container.EnsureIndex("score", new CancellationToken(true)));
        await Throws<InvalidOperationException>(async () => await container.FindByIndex<IndexRow>("score", 1));
        await container.EnsureIndex("score");
        await container.EnsureIndex("score");
        Check(await container.CountByIndex("score", 1) == 1, "Repeated EnsureIndex duplicated entries.");
    }

    [Test]
    public async Task Ranges_sort_and_page_before_materializing_with_deterministic_ties()
    {
        await using MemoryLibrarianDatabase database = CreateDatabase();
        ILibrarianContainer container = await database.GetContainer("items");
        for (var i = 0; i < 100; i++)
            await container.AddItem($"id-{i:D3}", $"{{\"id\":\"id-{i:D3}\",\"score\":{i / 2}}}");
        await container.EnsureIndex("score");
        LibrarianQueryResult<IndexRow> page = await container.FindRangeByIndex<IndexRow>("score", 20, 22, skip: 1, take: 3);
        Check(page.Items.Select(row => row.Id).SequenceEqual(new[] { "id-041", "id-042", "id-043" }), "Ascending range is wrong.");
        Check(page.DocumentsDeserialized == 3 && page.IndexEntriesExamined == 3, "Range did excess work.");
        LibrarianQueryResult<IndexRow> descending = await container.FindRangeByIndex<IndexRow>("score", 20, 22, descending: true, skip: 1, take: 3);
        Check(descending.Items.Select(row => row.Id).SequenceEqual(new[] { "id-044", "id-043", "id-042" }), "Descending range is wrong.");
        Check((await container.FindRangeByIndex<IndexRow>("score", minimum: 1000)).Items.Count == 0, "Out-of-range lookup matched.");
        Check((await container.FindRangeByIndex<IndexRow>("score", maximum: -1)).Items.Count == 0, "Negative bound matched.");
        Check((await container.FindRangeByIndex<IndexRow>("score", descending: true, take: 1)).Items[0].Score == 49, "Unbounded order failed.");
        await Throws<ArgumentException>(async () => await container.FindRangeByIndex<IndexRow>("score", 2, 1));
        await Throws<ArgumentException>(async () => await container.FindRangeByIndex<IndexRow>("score", 1, "2"));
        await Throws<ArgumentOutOfRangeException>(async () => await container.FindByIndex<IndexRow>("score", 1, skip: -1));
        await Throws<ArgumentOutOfRangeException>(async () => await container.FindByIndex<IndexRow>("score", 1, take: 0));
    }

    [Test]
    public async Task Concurrent_index_creation_writes_and_queries_preserve_consistent_results()
    {
        await using MemoryLibrarianDatabase database = CreateDatabase();
        ILibrarianContainer container = await database.GetContainer("items");
        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(async () =>
        {
            await container.EnsureIndex("status");
            await container.AddItem($"id-{i}", "{\"status\":\"old\"}");
            await container.UpdateItemStrict($"id-{i}", "{\"status\":\"new\"}");
            LibrarianQueryResult<IndexRow> page = await container.FindByIndex<IndexRow>("status", "new", take: 20);
            Check(page.Items.All(row => row.Status == "new"), "Index and document snapshots disagreed.");
        })));
        Check(await container.CountByIndex("status", "new") == 200 && await container.CountByIndex("status", "old") == 0,
            "Concurrent mutation lost index entries.");
    }

    [Test]
    public async Task Filesystem_reload_rebuilds_indexes_and_repository_uses_them()
    {
        string path = Path.Combine(Path.GetTempPath(), $"librarian-index-{Guid.NewGuid():N}.json");
        try
        {
            for (var pass = 0; pass < 2; pass++)
            {
                IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Librarian:FileSystem:FilePath"] = path
                }).Build();
                await using ServiceProvider services = new ServiceCollection().AddLogging().AddSingleton(config)
                    .AddFileSystemLibrarianDatabaseAsSingleton().BuildServiceProvider();
                var database = services.GetRequiredService<ILibrarianDatabase>();
                var repository = new LibrarianRepository<IndexRow>(config, NullLogger<LibrarianRepository<IndexRow>>.Instance, database, "items");
                await repository.EnsureIndex("score");
                if (pass == 0) await repository.AddItem(new IndexRow { Id = "one", Score = 10 });
                Check(await repository.CountByIndex("score", 10) == 1 && await repository.ExistsByIndex("score", 10), "Repository index lost data.");
                Check((await repository.FindByIndex("score", 10)).Items[0].Id == "one", "Repository equality failed.");
                Check((await repository.FindRangeByIndex("score", 9, 11)).Items.Count == 1, "Repository range failed.");
                await database.UnloadContainer("items");
                await Throws<InvalidOperationException>(async () => await repository.CountByIndex("score", 10));
                await repository.EnsureIndex("score");
                Check(await repository.CountByIndex("score", 10) == 1, "Reloaded index lost persisted data.");
            }
        }
        finally { await _fileUtil.Delete(path); }
    }
}
