using Soenneker.Utils.MemoryStream;
using Soenneker.Utils.File.Abstract;
using System;
using System.Linq;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Librarian.Memory;
using Soenneker.Librarian.FileSystem.Registrars;
using Soenneker.Librarian.Abstractions;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

[NotInParallel]
public class QueryableTests
{
    private static readonly IFileUtil _fileUtil = new Soenneker.Utils.File.FileUtil(NullLogger<Soenneker.Utils.File.FileUtil>.Instance, new MemoryStreamUtil());


    private static void Check(bool value) { if (!value) throw new Exception("Query assertion failed."); }

    [Test]
    public async ValueTask Deferred_queries_build_once_page_by_rank_and_count_without_deserializing(CancellationToken cancellationToken)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await database.GetContainer("query", cancellationToken: cancellationToken);
        for (var i = 0; i < 1000; i++) await container.AddItem(i.ToString(), $"{{\"score_value\":{i}}}", cancellationToken: cancellationToken);
        QueryableRow.Created = 0;
        IQueryable<QueryableRow> query = container.BuildQueryable<QueryableRow>();
        IQueryable<QueryableRow> page = query.Where(row => row.Score >= 100 && row.Score < 900).OrderByDescending(row => row.Score).Skip(700).Take(4);
        Check(QueryableRow.Created == 0);
        Check(page.Select(row => row.Score).SequenceEqual(new[] { 199, 198, 197, 196 }));
        Check(QueryableRow.Created == 1004);
        QueryableRow.Created = 0;
        Check(page.ToArray().Length == 4 && QueryableRow.Created == 4);
        QueryableRow.Created = 0;
        Check(query.Count(row => row.Score == 199) == 1);
        Check(query.Any(row => row.Score == 199));
        Check(page.LongCount() == 4);
        Check(QueryableRow.Created == 0);
        Check(query.First(row => row.Score == 999).Score == 999 && QueryableRow.Created == 1);
        QueryableRow.Created = 0;
        Check(query.SingleOrDefault(row => row.Score == 5000) is null && QueryableRow.Created == 0);
        var duplicateThrew = false;
        try { query.Single(row => row.Score >= 990); }
        catch (InvalidOperationException) { duplicateThrew = true; }
        Check(duplicateThrew && QueryableRow.Created == 2);
        var minimum = 997;
        IQueryable<QueryableRow> captured = query.Where(row => row.Score >= minimum);
        Check(captured.Count() == 3);
        minimum = 999;
        Check(captured.Count() == 1);
        Check(query.Where(row => row.Score > 4 && row.Score <= 4).Count() == 0);
        Check(query.OrderBy(row => row.Score).Take(5).Skip(20).Count() == 0);
        Check(query.OrderBy(row => row.Score).Skip(998).Take(10).Count() == 2);
    }

    [Test]
    public async ValueTask Automatic_indexes_follow_mutations_defaults_aliases_and_document_types(CancellationToken cancellationToken)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await database.GetContainer("query", cancellationToken: cancellationToken);
        await container.AddItem("default", "{}", cancellationToken: cancellationToken);
        await container.AddItem("aliased", "{\"SCORE_VALUE\":8}", cancellationToken: cancellationToken);
        await container.AddItem("invalid", "not json", cancellationToken: cancellationToken);
        IQueryable<QueryableRow> query = container.BuildQueryable<QueryableRow>();
        Check(query.Count(row => row.Score == 7) == 1);
        Check(query.Count(row => row.Score == 8) == 1);
        Check(query.Count(row => row.Name == null) == 2);
        await container.AddItem("new", "{\"score_value\":7,\"name\":\"Alex\"}", cancellationToken: cancellationToken);
        Check(query.Count(row => row.Score == 7) == 2);
        Check(query.Count(row => row.Name == "Alex") == 1);
        await container.UpdateItemStrict("new", "{\"score_value\":9}", cancellationToken: cancellationToken);
        Check(query.Count(row => row.Score == 7) == 1 && query.Count(row => row.Score == 9) == 1);
        Check(!query.Any(row => row.Name == "Alex"));
        await container.UpdateItemStrict("new", "invalid", cancellationToken: cancellationToken);
        Check(!query.Any(row => row.Score == 9));
        await container.DeleteItem("default", cancellationToken: cancellationToken);
        Check(!query.Any(row => row.Score == 7));
        await container.DeleteAllItems(cancellationToken: cancellationToken);
        Check(!query.Any(row => row.Score == 8));
        await container.AddItem("new", "{}", cancellationToken: cancellationToken);
        Check(query.Count(row => row.Score == 7) == 1);
        Check(container.BuildQueryable<OtherRow>().Count(row => row.Score == 0) == 1);
    }

    [Test]
    public async ValueTask Direct_element_execution_preserves_defaults_errors_and_detached_results(CancellationToken cancellationToken)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await database.GetContainer("query", cancellationToken: cancellationToken);
        await container.AddItem("one", "{\"score_value\":1}", cancellationToken: cancellationToken);
        IQueryable<QueryableRow> query = container.BuildQueryable<QueryableRow>();
        IQueryable<QueryableRow> selected = query.Where(row => row.Score == 1);
        QueryableRow first = selected.First();
        first.Score = 99;
        Check(selected.Single().Score == 1);
        Check(query.FirstOrDefault(row => row.Score == 2) is null);
        var threw = false;
        try { query.First(row => row.Score == 2); }
        catch (InvalidOperationException) { threw = true; }
        Check(threw);
        var fallback = new QueryableRow { Score = 42 };
        Check(query.Where(row => row.Score == 2).FirstOrDefault(fallback) == fallback);
        Check(query.Select(row => row.Score).FirstOrDefault() == 1);
        Check(query.Where(row => row.Score == 1).First(row => row.Computed == 1).Score == 1);
        container.Dispose();
        threw = false;
        try { selected.First(); }
        catch (ObjectDisposedException) { threw = true; }
        Check(threw);
    }

    [Test]
    public async ValueTask Writes_deserialize_once_per_type_for_multiple_automatic_indexes(CancellationToken cancellationToken)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await database.GetContainer("query", cancellationToken: cancellationToken);
        IQueryable<QueryableRow> query = container.BuildQueryable<QueryableRow>();
        query.Count(row => row.Score == 7);
        query.Count(row => row.Name == "Alex");
        QueryableRow.Created = 0;
        await container.AddItem("one", "{\"name\":\"Alex\"}", cancellationToken: cancellationToken);
        Check(QueryableRow.Created == 1);
        Check(query.Count(row => row.Score == 7) == 1 && query.Count(row => row.Name == "Alex") == 1);
        Check(QueryableRow.Created == 1);
        await container.UpdateItemStrict("one", "{\"score_value\":8,\"name\":\"Other\"}", cancellationToken: cancellationToken);
        Check(QueryableRow.Created == 2);
        Check(!query.Any(row => row.Score == 7) && !query.Any(row => row.Name == "Alex"));
        Check(query.Count(row => row.Score == 8) == 1 && query.Count(row => row.Name == "Other") == 1);
    }

    [Test]
    public async ValueTask Concurrent_creation_writes_and_reads_keep_indexes_consistent(CancellationToken cancellationToken)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await database.GetContainer("query", cancellationToken: cancellationToken);
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
        {
            await container.AddItem(i.ToString(), $"{{\"score_value\":{i}}}", cancellationToken: cancellationToken);
            Check(container.BuildQueryable<QueryableRow>().Any(row => row.Score == i));
            await container.UpdateItemStrict(i.ToString(), $"{{\"score_value\":{i + 100}}}", cancellationToken: cancellationToken);
            Check(!container.BuildQueryable<QueryableRow>().Any(row => row.Score == i));
        }, cancellationToken: cancellationToken)));
        Check(container.BuildQueryable<QueryableRow>().Where(row => row.Score >= 100).Count() == 100);
    }

    [Test]
    public async ValueTask Filesystem_queries_rebuild_automatically_after_restart(CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetTempPath(), $"librarian-linq-{Guid.NewGuid():N}.json");
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
                ILibrarianContainer container = await database.GetContainer("query", cancellationToken: cancellationToken);
                if (pass == 0) await container.AddItem("one", "{\"score_value\":12}", cancellationToken: cancellationToken);
                Check(container.BuildQueryable<QueryableRow>().Where(row => row.Score == 12).Single().Score == 12);
                await database.Save(cancellationToken: cancellationToken);
            }
        }
        finally { await _fileUtil.Delete(path); }
    }

    [Test]
    public async ValueTask Unsupported_operations_preserve_their_position_and_normal_linq_semantics(CancellationToken cancellationToken)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await database.GetContainer("query", cancellationToken: cancellationToken);
        for (var i = 0; i < 20; i++) await container.AddItem(i.ToString(), $"{{\"score_value\":{i},\"name\":\"name-{i}\"}}", cancellationToken: cancellationToken);
        IQueryable<QueryableRow> query = container.BuildQueryable<QueryableRow>();
        Check(query.Where(row => row.Score >= 5).Where(row => row.Computed == 0).OrderBy(row => row.Score).Select(row => row.Score)
            .SequenceEqual(Enumerable.Range(5, 15).Where(i => i % 2 == 0)));
        Check(query.OrderBy(row => row.Score).Take(3).Where(row => row.Score > 10).Count() == 0);
        Check(query.Where(row => row.Score > 17 || row.Score < 2).Count() == 4);
        Check(query.Where(row => row.Name!.EndsWith("9")).Count() == 2);
        Check(query.Where(row => row.Score >= 5 && row.Name == "name-7").Single().Score == 7);
        Check(query.Where(row => row.Score == 1).Concat(query).Count() == 21);
        Check(query.Select(row => row.Score).Count(value => value > 10) == 9);
        Check(query.Where(row => row.Score >= 10).Take(0).Count() == 0);
        Check(query.OrderBy(row => row.Score).Skip(-1).Take(1).Single().Score == 0);
    }
}
