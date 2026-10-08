using Soenneker.Cloudflare.R2;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Memory;
using Soenneker.Librarian.Abstractions.Transactions;
using Soenneker.Librarian.R2;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class AdditionalVersionedProviderTests
{
    [Test]
    public async Task Memory_versions_guard_mutations_and_recreation(CancellationToken cancellationToken)
    {
        await using var database = new MemoryLibrarianDatabase(NullLogger<MemoryLibrarianDatabase>.Instance);
        ILibrarianContainer container = await database.GetContainer("items", cancellationToken: cancellationToken);
        await VersionedProviderContract.Exercise(container, container);
    }

    [Test]
    [Arguments("d1")]
    [Arguments("kv")]
    [Arguments("r2")]
    public async Task Snapshot_versions_guard_mutations_within_the_single_owner(string provider, CancellationToken cancellationToken)
    {
        using var fixture = new CloudflareFixture(provider);
        await using ILibrarianDatabase database = fixture.Create();
        ILibrarianContainer container = await database.GetContainer("items", cancellationToken: cancellationToken);
        await VersionedProviderContract.Exercise(container, container);
    }

    [Test]
    public async Task FileSystem_versions_guard_mutations(CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture("filesystem");
        ILibrarianContainer container = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await VersionedProviderContract.Exercise(container, container);
    }

    [Test]
    [Arguments("memory")]
    [Arguments("filesystem")]
    [Arguments("redis")]
    [Arguments("postgres")]
    public async Task Ordinary_and_batch_writes_invalidate_versions_even_for_identical_content(string provider, CancellationToken cancellationToken)
    {
        await using var fixture = new BatchFixture(provider);
        ILibrarianContainer container = await fixture.Database.GetContainer("items", cancellationToken: cancellationToken);
        await container.AddItem("one", "same", cancellationToken: cancellationToken);
        LibrarianItem<string> before = (await container.GetItemWithVersion("one", cancellationToken: cancellationToken))!;
        await container.UpdateItem("one", "same", cancellationToken: cancellationToken);
        Check(!await container.DeleteItemIfVersion("one", before.Version, cancellationToken: cancellationToken), "An ordinary identical write retained its old revision.");
        before = (await container.GetItemWithVersion("one", cancellationToken: cancellationToken))!;
        Check(await fixture.Database.Execute(new LibrarianBatch([new LibrarianWrite("items", "one", "same")]), cancellationToken: cancellationToken), "Batch failed.");
        Check(!await container.DeleteItemIfVersion("one", before.Version, cancellationToken: cancellationToken), "A batch identical write retained its old revision.");
    }

    [Test]
    public async Task Redis_versions_coordinate_independent_instances(CancellationToken cancellationToken)
    {
        await using var fixture = new RedisPersistenceFixture();
        await using var second = fixture.CreateDatabase();
        await VersionedProviderContract.Exercise(await fixture.Database.GetContainer("items", cancellationToken: cancellationToken), await second.GetContainer("items", cancellationToken: cancellationToken));
    }

    [Test]
    public async Task Postgres_versions_coordinate_independent_instances(CancellationToken cancellationToken)
    {
        await using var fixture = new PostgresPersistenceFixture();
        await using var second = fixture.CreateDatabase();
        await VersionedProviderContract.Exercise(await fixture.Database.GetContainer("items", cancellationToken: cancellationToken), await second.GetContainer("items", cancellationToken: cancellationToken));
    }

    [Test]
    public async Task R2_objects_coordinate_independent_instances_and_preserve_unknown_outcomes(CancellationToken cancellationToken)
    {
        using var transport = new R2ObjectHttpHandler();
        using var http = new HttpClient(transport);
        var store = new CloudflareR2WorkerObjectStore(http, new Uri("https://r2.example/"), "test-token");
        await using var first = new R2ObjectLibrarianDatabase(store);
        await using var second = new R2ObjectLibrarianDatabase(store);
        ILibrarianContainer a = await first.GetContainer("items", cancellationToken: cancellationToken);
        ILibrarianContainer b = await second.GetContainer("items", cancellationToken: cancellationToken);
        await VersionedProviderContract.Exercise(a, b);
        Check((await a.GetAllIds(cancellationToken: cancellationToken)).Count == 1, "Listings lost a live document or exposed tombstones.");
        LibrarianItem<string> current = (await a.GetItemWithVersion("counter", cancellationToken: cancellationToken))!;
        Check(await b.DeleteItemIfVersion("counter", current.Version, cancellationToken: cancellationToken), "Conditional tombstone failed.");
        Check((await a.GetAllIds(cancellationToken: cancellationToken)).Count == 0, "Tombstone appeared in a listing.");
        await a.AddItem("counter", NativeDocumentJson.Create("counter", "{\"score_value\":0}", "org-a"), cancellationToken: cancellationToken);
        transport.LoseNextWriteResponse = true;
        int before = transport.Writes;
        try
        {
            await a.MutateItem<NativeDocument>("counter", item => { item.Score++; return item; }, cancellationToken: cancellationToken);
            throw new Exception("Lost response was treated as a confirmed outcome.");
        }
        catch (HttpRequestException) { }
        Check(transport.Writes == before + 1, "An uncertain write was retried.");
        Check((await b.GetItemWithVersion<NativeDocument>("counter", cancellationToken: cancellationToken))!.Document.Score == 1, "Committed write was lost.");
    }
}
