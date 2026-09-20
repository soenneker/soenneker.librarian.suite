using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Redis;
using Soenneker.Librarian.Redis.Registrars;

namespace Soenneker.Librarian.Suite.Tests;

// Independent fixtures share Redis and its client worker pool. Keep concurrency inside each test deliberate.
[NotInParallel("Redis")]
public class RedisPersistenceTests
{
    [Test]
    public async Task Readable_keys_support_custom_prefixes_and_escape_reserved_characters()
    {
        await using var fixture = new RedisPersistenceFixture("flywheel");
        var store = await fixture.GetStore();
        ILibrarianContainer jobs = await fixture.Database.GetContainer("flywheel.jobs");
        await jobs.AddItem("job-123", "{\"value\":{\"state\":\"scheduled\"}}");
        await jobs.EnsureIndex("value.state");
        string prefix = fixture.RedisPrefix + "flywheel.jobs:";
        Check(await store.HashGetAsync(prefix + "document:JOB-123", "id") == "job-123", "Document key is not readable.");
        Check(await store.KeyExistsAsync(prefix + "index:value.state"), "Index path is not readable.");
        Check(await jobs.CountByIndex("value.state", "scheduled") == 1, "Readable index failed.");
        await using var other = new RedisLibrarianDatabase(fixture.Key, _ => ValueTask.FromResult(store), keyPrefix: "flywheel");
        Check(await (await other.GetContainer("flywheel.jobs")).GetItem("JOB-123") is not null, "Factory prefix differs from configuration prefix.");
        await using var isolated = new RedisLibrarianDatabase(fixture.Key, _ => ValueTask.FromResult(store));
        Check(await (await isolated.GetContainer("flywheel.jobs")).GetItem("job-123") is null, "Prefixes are not isolated.");
        Check((await other.GetServerTime()).Year >= 2026, "Server time failed with a custom prefix.");

        ILibrarianContainer special = await fixture.Database.GetContainer("jobs:*->{x}%");
        string[] ids = ["a:b", "a%003Ab", "*->json", "{x}", "~", "é", ""];
        foreach (string id in ids) await special.AddItem(id, "{\"name\":\"same\"}");
        await special.EnsureIndex("name");
        Check((await special.GetAllIds()).Count == ids.Length, "Escaped IDs collided or broke SORT.");
        Check(await special.CountByIndex("name", "same") == ids.Length, "Escaped IDs exceeded index bounds.");
        Check((await special.FindByIndex<RedisRow>("name", "same")).Items.Count == ids.Length, "Indexed reads lost escaped IDs.");
        foreach (string id in ids) Check(await special.GetItem(id) is not null, "Escaped ID cannot be read.");
        await special.DeleteAllItems();
        Check(await special.CountByIndex("name", "same") == 0, "Escaped IDs survived clear.");
    }

    [Test]
    public async Task Mutations_are_visible_immediately_across_instances_without_save()
    {
        await using var fixture = new RedisPersistenceFixture();
        await using RedisLibrarianDatabase other = fixture.CreateDatabase();
        ILibrarianContainer first = await fixture.Database.GetContainer("items");
        ILibrarianContainer second = await other.GetContainer("items");
        await first.AddItem("One", "original");
        Check(await second.GetItem("ONE") == "original", "Add was not stored immediately.");
        await second.UpdateItemStrict("one", "updated");
        Check(await first.GetItem("One") == "updated", "Read used stale local data.");
        Check((await first.GetAllIds()).Single() == "One", "Original ID spelling was lost.");
        Check((await first.GetLibrarianItems()).Single().Value == "updated", "Pairs read stale data.");
        Check(await first.UpdateItem("missing", "value") is null, "Update inserted missing data.");
        await second.DeleteItem("ONE");
        Check(await first.GetItem("one") is null, "Delete was not immediate.");
        await first.AddItem("two", "second");
        await second.DeleteAllItems();
        Check((await first.GetAllItems()).Count == 0, "Clear was not immediate.");
        await first.AddItem("three", "survives unload");
        await fixture.Database.UnloadContainer("items");
        try { await first.GetItem("three"); throw new Exception("Unloaded handle remained usable."); }
        catch (ObjectDisposedException) { }
        Check(await second.GetItem("three") == "survives unload", "Unloading removed Redis data.");
        Check(await (await other.GetContainer("Items")).GetItem("three") is null, "Container names collided.");
    }

    [Test]
    public async Task Concurrent_adds_are_atomic_and_concurrent_updates_do_not_lose_other_documents()
    {
        await using var fixture = new RedisPersistenceFixture();
        await using RedisLibrarianDatabase other = fixture.CreateDatabase();
        ILibrarianContainer first = await fixture.Database.GetContainer("items");
        ILibrarianContainer second = await other.GetContainer("items");
        var added = 0;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            try { await (i % 2 == 0 ? first : second).AddItem("same", "value"); Interlocked.Increment(ref added); }
            catch (InvalidOperationException) { }
        }));
        Check(added == 1, "Duplicate check was not atomic.");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i => await (i % 2 == 0 ? first : second).AddItem(i.ToString(), "value")));
        Check((await first.GetAllItems()).Count == 21, "Concurrent writes lost documents.");
    }

    [Test]
    public async Task Redis_indexes_persist_and_track_other_instances_writes()
    {
        await using var fixture = new RedisPersistenceFixture();
        await using RedisLibrarianDatabase other = fixture.CreateDatabase();
        ILibrarianContainer first = await fixture.Database.GetContainer("items");
        ILibrarianContainer second = await other.GetContainer("items");
        await first.AddItem("a", "{\"amount\":1.0000000000000000000000000001,\"name\":\"a\"}");
        await first.AddItem("b", "{\"amount\":1.0000000000000000000000000002,\"name\":\"aa\"}");
        await first.AddItem("c", "{\"amount\":-79228162514264337593543950335,\"name\":\"z\"}");
        await first.EnsureIndex("amount");
        await first.EnsureIndex("name");
        Check(await second.CountByIndex("amount", 1.0000000000000000000000000001m) == 1, "Decimal index lost precision.");
        LibrarianQueryResult<RedisRow> page = await second.FindRangeByIndex<RedisRow>("amount", -79228162514264337593543950335m, 1.0000000000000000000000000002m, skip: 1, take: 1);
        Check(page.Items.Single().Name == "a" && page.DocumentsDeserialized == 1, "Server range paging failed.");
        Check((await second.FindRangeByIndex<RedisRow>("name", "a", "aa")).Items.Count == 2, "String prefix ordering failed.");
        await second.UpdateItemStrict("a", "{\"amount\":2,\"name\":\"changed\"}");
        Check(!await first.ExistsByIndex("amount", 1.0000000000000000000000000001m), "Old index entry survived update.");
        Check(await first.ExistsByIndex("amount", 2), "New index entry is missing.");
        await second.DeleteItem("b");
        Check(!await first.ExistsByIndex("name", "aa"), "Delete left index entry.");
        await first.DeleteAllItems();
        Check(await second.CountByIndex("amount", 2) == 0, "Clear left index entries.");
        await second.AddItem("d", "{\"amount\":3}");
        Check(await first.ExistsByIndex("amount", 3), "Clear dropped index definitions.");
    }

    [Test]
    public async Task Queries_execute_against_current_Redis_data_and_reject_local_fallback()
    {
        await using var fixture = new RedisPersistenceFixture();
        await using RedisLibrarianDatabase other = fixture.CreateDatabase();
        ILibrarianContainer first = await fixture.Database.GetContainer("items");
        ILibrarianContainer second = await other.GetContainer("items");
        for (var i = 0; i < 8; i++) await first.AddItem(i.ToString(), $"{{\"amount\":{i},\"active\":{(i % 2 == 0 ? "true" : "false")},\"name\":\"row-{i}\"}}");
        IQueryable<RedisRow> query = first.BuildQueryable<RedisRow>().Where(row => row.Active && row.Amount >= 2 && row.Amount <= 6).OrderByDescending(row => row.Amount).Skip(1).Take(2);
        Check(query.ToList().Select(row => row.Amount).SequenceEqual(new decimal[] { 4, 2 }), "Redis LINQ paging failed.");
        await second.UpdateItemStrict("4", "{\"amount\":1,\"active\":true,\"name\":\"updated\"}");
        Check(query.Count() == 1 && query.First().Amount == 2, "Re-executed query used stale data.");
        Check(first.BuildQueryable<RedisRow>().Where(row => row.Amount == 7 || row.Amount == 3).Count() == 2, "OR query failed.");
        Check(first.BuildQueryable<RedisRow>().Where(row => !row.Active).Count() == 4, "NOT query failed.");
        Check(first.BuildQueryable<RedisRow>().Any(row => row.Amount > 6), "Any query failed.");
        Check(first.BuildQueryable<RedisRow>().Where(row => row.Amount == 7).Single().Name == "row-7", "Single query failed.");
        Check(first.BuildQueryable<RedisRow>().Take(0).Count() == 0, "Take zero failed.");
        try { _ = first.BuildQueryable<RedisRow>().Where(row => row.Name.EndsWith("row")).ToList(); throw new Exception("Local fallback was accepted."); }
        catch (NotSupportedException) { }
        try { _ = first.BuildQueryable<RedisRow>().Take(2).Where(row => row.Amount > 1).ToList(); throw new Exception("Filter after paging changed semantics."); }
        catch (NotSupportedException) { }
    }

    [Test]
    public async Task Invalid_indexed_writes_and_cancellation_leave_Redis_unchanged()
    {
        await using var fixture = new RedisPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items");
        await container.AddItem("a", "{\"amount\":1}");
        await container.EnsureIndex("amount");
        try { await container.UpdateItemStrict("a", "{\"amount\":{}}"); throw new Exception("Invalid indexed value accepted."); }
        catch (ArgumentException) { }
        Check(await container.GetItem("a") == "{\"amount\":1}" && await container.ExistsByIndex("amount", 1), "Rejected write changed data.");
        try { await container.AddItem("b", "{}", new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Check(await container.GetItem("b") is null, "Cancelled write committed.");
        try { await container.CountByIndex("missing", 1); throw new Exception("Missing index accepted."); }
        catch (InvalidOperationException) { }
    }

    [Test]
    public async Task Service_registrations_share_live_data_and_save_is_a_noop()
    {
        await using var fixture = new RedisPersistenceFixture();
        IServiceCollection services = new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(fixture.Configuration).AddRedisLibrarianDatabaseAsScoped();
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using AsyncServiceScope scope1 = provider.CreateAsyncScope();
        await using AsyncServiceScope scope2 = provider.CreateAsyncScope();
        var first = scope1.ServiceProvider.GetRequiredService<ILibrarianDatabase>();
        var second = scope2.ServiceProvider.GetRequiredService<ILibrarianDatabase>();
        await (await first.GetContainer("items")).AddItem("one", "value");
        Check(await (await second.GetContainer("items")).GetItem("one") == "value", "Scopes did not see immediate writes.");
        await first.Save();
        await first.MarkDirty("items");
    }

    [Test]
    public async Task Concurrent_index_creation_and_writes_preserve_all_index_entries()
    {
        await using var fixture = new RedisPersistenceFixture();
        await using RedisLibrarianDatabase other = fixture.CreateDatabase();
        ILibrarianContainer first = await fixture.Database.GetContainer("items");
        ILibrarianContainer second = await other.GetContainer("items");
        for (var i = 0; i < 10; i++) await first.AddItem(i.ToString(), "{\"amount\":1}");
        await Task.WhenAll(
            first.EnsureIndex("amount").AsTask(),
            second.EnsureIndex("amount").AsTask(),
            Task.Run(async () =>
            {
                for (var i = 10; i < 30; i++) await second.AddItem(i.ToString(), "{\"amount\":1}");
            }));
        Check(await first.CountByIndex("amount", 1) == 30, "Index creation raced with writes and lost entries.");
        await other.UnloadContainer("items");
        Check(await (await other.GetContainer("items")).CountByIndex("amount", 1) == 30, "Index was only local.");
    }

    [Test]
    public async Task Null_missing_boolean_and_string_indexes_preserve_scalar_order()
    {
        await using var fixture = new RedisPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items");
        await container.AddItem("null", "{\"value\":null}");
        await container.AddItem("missing", "{}");
        await container.AddItem("false", "{\"value\":false}");
        await container.AddItem("true", "{\"value\":true}");
        await container.AddItem("negative", "{\"value\":-1}");
        await container.AddItem("empty", "{\"value\":\"\"}");
        await container.AddItem("prefix", "{\"value\":\"a\"}");
        await container.AddItem("longer", "{\"value\":\"aa\"}");
        await container.EnsureIndex("value");
        Check(await container.CountByIndex("value", null) == 1, "Missing and null values were conflated.");
        LibrarianQueryResult<JsonElement> page = await container.FindRangeByIndex<System.Text.Json.JsonElement>("value", take: 20);
        Check(page.Items.Select(item => item.GetProperty("value").GetRawText()).SequenceEqual(new[] { "null", "false", "true", "-1", "\"\"", "\"a\"", "\"aa\"" }), "Scalar ordering changed.");
    }
    [Test]
    public async Task Query_values_use_the_same_generated_contracts_as_documents()
    {
        await using var fixture = new RedisPersistenceFixture();
        ILibrarianContainer container = await fixture.Database.GetContainer("items");
        string json = JsonSerializer.Serialize(new RedisRow { Status = RedisStatus.Active, DisplayName = "Visible" }, TestJsonContext.Default.RedisRow);
        await container.AddItem("one", json);
        await container.EnsureIndex("status");
        Check(await container.CountByIndex("status", RedisStatus.Active) == 1, "Enum query value did not match the generated string-enum encoding.");
        Check(container.BuildQueryable<RedisRow>().Single(row => row.DisplayName == "Visible").Status == RedisStatus.Active,
            "JSON property name or deserialization options differed.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
