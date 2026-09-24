using System.Linq.Expressions;
using Microsoft.Extensions.Configuration;
using Soenneker.Documents.Document;
using Soenneker.Librarian.Core;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Librarian.FileSystem;
using Soenneker.Librarian.Memory;
using Soenneker.Librarian.Postgres;
using Soenneker.Librarian.Redis;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.File.Registrars;
using Soenneker.Utils.MemoryStream.Abstract;
using StackExchange.Redis;

LibrarianJson.Register(SmokeJsonContext.Default.SmokeRow);
LibrarianJson.Register(SmokeJsonContext.Default.SmokeStatus);
LibrarianJson.Register(SmokeJsonContext.Default.SmokeDocument);
await using (var memory = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance))
{
    await CheckDatabase(memory);
    var repository = new LibrarianRepository<SmokeDocument>(new ConfigurationBuilder().Build(),
        NullLogger<LibrarianRepository<SmokeDocument>>.Instance, memory, "repository");
    await repository.AddItem(new SmokeDocument { Id = "one", Name = "generated" });
    Check((await repository.GetItem("one"))?.Name == "generated", "Repository source-generated CRUD");
}

string path = Path.Combine(Path.GetTempPath(), $"librarian-aot-{Guid.NewGuid():N}.json");
await using var services = new ServiceCollection().AddLogging().AddFileUtilAsSingleton().BuildServiceProvider();
try
{
    await using (var file = new FileSystemLibrarianDatabase(path, services.GetRequiredService<IFileUtil>(),
        services.GetRequiredService<IMemoryStreamUtil>(), NullLogger.Instance))
    {
        await CheckDatabase(file);
        await file.Save();
    }
    await using (var file = new FileSystemLibrarianDatabase(path, services.GetRequiredService<IFileUtil>(),
        services.GetRequiredService<IMemoryStreamUtil>(), NullLogger.Instance))
        Check((await file.GetContainer("rows")).BuildQueryable<SmokeRow>().Count() == 6, "Persistence reload");
}
finally { await services.GetRequiredService<IFileUtil>().Delete(path); }

// Exercise both remote translators and materializers without requiring live services.
var redisProvider = new RedisQueryProvider<SmokeRow>(null!);
var redis = new RedisQueryable<SmokeRow>(redisProvider);
var selected = redis.OrderBy(row => row.Score).Take(2).Select(row => new SmokeProjection(row.Name, row.Score));
RedisQueryPlan redisPlan = RedisQueryPlan.Create(selected.Expression, redisProvider);
object? projected = redisPlan.Projection!.FromDocument("{\"score\":2,\"name\":\"row-2\",\"status\":\"Active\"}");
Check(projected is SmokeProjection { Name: "row-2", Score: 2 }, "Redis record projection");
var missing = RedisQueryPlan.Create(redis.Take(1).Select(row => row.Score).Expression, redisProvider);
Check(Equals(missing.Projection!.FromDocument("{}"), 0), "Redis missing scalar default");
var enumProjection = RedisQueryPlan.Create(redis.Take(1).Select(row => row.Status).Expression, redisProvider);
Check(Equals(enumProjection.Projection!.FromDocument("{\"status\":\"Inactive\"}"), SmokeStatus.Inactive), "Redis generated enum projection");
var set = new HashSet<int> { 1, 2 };
RedisQueryPlan.Create(redis.Where(row => set.Contains(row.Score)).Expression, redisProvider);
var postgresProvider = new PostgresQueryProvider<SmokeRow>(null!);
var postgres = new PostgresQueryable<SmokeRow>(postgresProvider);
var composed = postgres.Select(row => row.Score + 1).Where(score => score > 1).Take(2);
PostgresQueryPlan postgresPlan = PostgresQueryPlan.Create(composed.Expression, postgresProvider);
Check(Equals(postgresPlan.Projection!.Materialize(["2"]), 2), "Postgres composed scalar projection");
PostgresQueryPlan.Create(postgres.Where(row => set.Contains(row.Score)).Expression, postgresProvider);
if (Environment.GetEnvironmentVariable("LIBRARIAN_TEST_REDIS") is { Length: > 0 } redisConnection)
{
    using var connection = await ConnectionMultiplexer.ConnectAsync(redisConnection);
    await using var database = new RedisLibrarianDatabase($"aot-{Guid.NewGuid():N}", _ => ValueTask.FromResult(connection.GetDatabase()));
    await CheckRemote(database);
    Console.WriteLine("Native Redis I/O passed.");
}
if (Environment.GetEnvironmentVariable("LIBRARIAN_TEST_POSTGRES") is { Length: > 0 } postgresConnection)
{
    await using var database = new PostgresLibrarianDatabase(postgresConnection, $"aot-{Guid.NewGuid():N}");
    await CheckRemote(database);
    Console.WriteLine("Native PostgreSQL I/O passed.");
}
Console.WriteLine("Native AOT smoke passed: memory, filesystem, Redis/Postgres translation and projection.");

static async Task CheckDatabase(ILibrarianDatabase database)
{
    ILibrarianContainer container = await database.GetContainer("rows");
    for (var i = 0; i < 6; i++)
        await container.AddItem(i.ToString(), JsonSerializer.Serialize(new SmokeRow { Score = i, Name = $"row-{i}", Status = SmokeStatus.Active }, SmokeJsonContext.Default.SmokeRow));
    IQueryable<SmokeRow> root = container.BuildQueryable<SmokeRow>();
    Check(root.Count(row => row.Score >= 2) == 4, "Indexed count");
    Check(root.Count(row => row.LongScore == 0) == 6, "Long automatic index");
    Check(root.Count(row => row.Amount == 0m) == 6, "Decimal automatic index");
    Check(root.Count(row => !row.Active) == 6, "Boolean automatic index");
    int minimum = 2;
    IQueryable<SmokeRow> captured = root.Where(row => row.Score >= minimum);
    Check(captured.Count() == 4, "Captured field"); minimum = 4;
    Check(captured.Count() == 2, "Live captured field");
    Check(root.OrderBy(row => row.Score).Skip(1).Take(2).Select(row => row.Score).SequenceEqual(new[] { 1, 2 }), "Projected paging");
    Check(root.OrderBy(row => row.Status).ThenByDescending(row => row.Score).First().Score == 5, "Fallback ordering");
    Check(root.Select((row, index) => row.Score + index).Count(value => value >= 4) == 4, "Indexed projection");
    string[] names = ["row-1", "row-3"];
    Check(root.Where(row => names.Contains(row.Name)).Count() == 2, "Array membership interpretation");
    Check(await root.AllAsync(row => row.Score >= 0), "Async All");
    Check((await root.Where(row => row.Score > 3).Select(row => new SmokeProjection(row.Name, row.Score)).ToListAsync()).Count == 2, "Record projection");
    Check(root.Select(row => row.Score).Sum() == 15, "Aggregate");
    Check(root.Select(row => row.Score).Where(value => value > 10).FirstOrDefault() == 0, "Value-type default");
    Check(root.Where(row => row.Score < 0).FirstOrDefault() is null, "Reference default");
    Check(root.Provider.CreateQuery(root.Where(row => row.Score == 2).Expression).Cast<SmokeRow>().Single().Score == 2, "Non-generic query creation");
    await container.EnsureIndex("score");
    Check((await container.FindByIndex<SmokeRow>("score", 3)).Items.Single().Score == 3, "Explicit index");
    await container.UpdateItemStrict("2", "{\"score\":22,\"name\":\"changed\",\"status\":\"Active\"}");
    Check(root.Count(row => row.Score >= 20) == 1, "Index maintenance");
}

static async Task CheckRemote(ILibrarianDatabase database)
{
    ILibrarianContainer container = await database.GetContainer("rows");
    try
    {
        for (var i = 0; i < 6; i++)
            await container.AddItem(i.ToString(), JsonSerializer.Serialize(new SmokeRow { Score = i, Name = $"row-{i}", Status = SmokeStatus.Active }, SmokeJsonContext.Default.SmokeRow));
        IQueryable<SmokeRow> root = container.BuildQueryable<SmokeRow>();
        var page = await root.Where(row => row.Score >= 2).OrderBy(row => row.Score).Take(2)
            .Select(row => new SmokeProjection(row.Name, row.Score)).ToListAsync();
        Check(page.Count == 2 && page[0].Score == 2, "Remote record projection");
        Check((await root.Take(2).ToListAsync()).Count == 2, "Remote typed document list");
        Check(await root.AllAsync(row => row.Score >= 0), "Remote async All");
        var values = new HashSet<int> { 1, 4 };
        Check(await root.CountAsync(row => values.Contains(row.Score)) == 2, "Remote set membership");
        Check(await root.Where(row => row.Score > 10).Select(row => row.Score).FirstOrDefaultAsync() == 0, "Remote scalar default");
        await container.EnsureIndex("status");
        Check(await container.CountByIndex("status", SmokeStatus.Active) == 6, "Remote enum index");
        await container.EnsureIndex("score");
        Check((await container.FindByIndex<SmokeRow>("score", 3)).Items.Single().Score == 3, "Remote typed index page");
        await container.UpdateItemStrict("3", "{\"score\":30,\"name\":\"changed\",\"status\":\"Active\"}");
        Check(await root.CountAsync(row => row.Score == 30) == 1, "Remote index maintenance");
    }
    finally { await container.DeleteAllItems(); }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

public enum SmokeStatus { Active, Inactive }
public sealed class SmokeRow
{
    public int Score { get; set; }
    public string Name { get; set; } = "";
    public SmokeStatus Status { get; set; }
    public long LongScore { get; set; }
    public decimal Amount { get; set; }
    public bool Active { get; set; }
}
public sealed class SmokeDocument : Document { public string Name { get; set; } = ""; }
public sealed record SmokeProjection(string Name, int Score);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SmokeRow))]
[JsonSerializable(typeof(SmokeStatus))]
[JsonSerializable(typeof(SmokeDocument))]
internal partial class SmokeJsonContext : JsonSerializerContext;
