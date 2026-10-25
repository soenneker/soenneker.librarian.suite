using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Librarian.FileSystem;
using Soenneker.Librarian.Redis;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Librarian.Mongo;
using MongoDB.Bson.Serialization;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class ReliabilityTests
{
    [Test]
    public async Task Failed_final_flush_can_be_retried(CancellationToken cancellationToken)
    {
        await using var fixture = new ReliabilityFixture();
        var database = fixture.Resolve<FileSystemLibrarianDatabase>();
        var container = await database.GetContainer("rows", cancellationToken);
        await container.AddItem("pending", "{\"score\":7}", cancellationToken);
        // Invalid persisted JSON produces a real storage failure on every supported platform.
        await File.WriteAllTextAsync(fixture.Path, "invalid", cancellationToken);
        bool failed = false;
        try { await database.DisposeAsync(); }
        catch (System.Text.Json.JsonException) { failed = true; }
        if (!failed) throw new Exception("The failing final flush was swallowed.");
        await File.WriteAllTextAsync(fixture.Path, "{}", cancellationToken);
        await database.DisposeAsync();
        await database.DisposeAsync();
        if (!(await File.ReadAllTextAsync(fixture.Path, cancellationToken)).Contains("pending"))
            throw new Exception("Retrying disposal lost the pending document.");
    }

    [Test]
    public async Task Redis_queries_follow_registered_and_nested_naming_policies(CancellationToken cancellationToken)
    {
        LibrarianJson.Register(PascalCaseJsonContext.Default.PascalCaseRow);
        LibrarianJson.Register(SnakeCaseJsonContext.Default.SnakeCaseRow);
        await using var fixture = new ReliabilityFixture(redis: true);
        var database = fixture.Resolve<RedisLibrarianDatabase>();
        var pascal = await database.GetContainer("pascal", cancellationToken);
        var snake = await database.GetContainer("snake", cancellationToken);
        await pascal.AddItem("one", LibrarianJson.Serialize(new PascalCaseRow(7, new(9))), cancellationToken);
        await snake.AddItem("one", LibrarianJson.Serialize(new SnakeCaseRow(7, new(9))), cancellationToken);
        var captured = new PascalCaseRow(7, new(9));
        if (await pascal.BuildQueryable<PascalCaseRow>().Count(row => row.Score == captured.Score, cancellationToken) != 1)
            throw new Exception("A captured value was mistaken for a document path.");
        if (await pascal.BuildQueryable<PascalCaseRow>().Count(row => row.Score == 7 && row.Nested.InnerScore == 9, cancellationToken) != 1 ||
            await snake.BuildQueryable<SnakeCaseRow>().Count(row => row.TotalScore == 7 && row.Nested.InnerScore == 9, cancellationToken) != 1)
            throw new Exception("Query paths disagreed with the registered JSON contracts.");
    }

    [Test]
    public void Generated_mongo_mappings_follow_the_serialization_options()
    {
        var registry = new MongoJsonSerializerRegistry();
        Soenneker.Librarian.Generated.LibrarianModels.RegisterMongo(registry);
        var serializer = (IBsonDocumentSerializer)registry.Create(typeof(SnakeCaseRow), SnakeCaseJsonContext.Default.Options);
        if (!serializer.TryGetMemberSerializationInfo("TotalScore", out var member) || member.ElementName != "total_score")
            throw new Exception("Generated Mongo mapping ignored the serialization naming policy.");
    }
}
