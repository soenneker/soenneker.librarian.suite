using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.CouchDb;
using static Soenneker.Librarian.Suite.Tests.DocumentProviderAssertions;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class CouchDbIntegrationTests
{
    [Test]
    public async Task Native_storage_provisioning_revisions_indexes_and_scopes(CancellationToken cancellationToken)
    {
        string? endpoint = Environment.GetEnvironmentVariable("LIBRARIAN_TEST_COUCHDB");
        if (string.IsNullOrEmpty(endpoint)) Skip.Test("Set LIBRARIAN_TEST_COUCHDB to run CouchDB integration tests.");
        string key = "test-" + Guid.NewGuid().ToString("N");
        var options = new CouchDbLibrarianOptions
        {
            Endpoint = new Uri(endpoint!), Key = key,
            Username = Environment.GetEnvironmentVariable("LIBRARIAN_TEST_COUCHDB_USERNAME"),
            Password = Environment.GetEnvironmentVariable("LIBRARIAN_TEST_COUCHDB_PASSWORD")
        };
        await using var httpClientCache = new Soenneker.Utils.HttpClientCache.HttpClientCache();
        await using var database = new CouchDbLibrarianDatabase(options, httpClientCache);
        await using var second = new CouchDbLibrarianDatabase(options, httpClientCache);
        string physical = "librarian-" + Convert.ToHexStringLower(Encoding.UTF8.GetBytes(key)) + "-6974656d73";
        try
        {
            ILibrarianContainer a = await database.GetContainer("items", "org-a", cancellationToken: cancellationToken);
            ILibrarianContainer b = await database.GetContainer("items", "org-b", cancellationToken: cancellationToken);
            for (int i = 0; i < 270; i++)
                await a.AddItem(i.ToString(), NativeDocumentJson.Create(i.ToString(), $"{{\"score_value\":{i}}}", "org-a"), cancellationToken: cancellationToken);
            await b.AddItem("0", NativeDocumentJson.Create("0", "{\"score_value\":999}", "org-b"), cancellationToken: cancellationToken);
            Check(await a.CountItems(cancellationToken: cancellationToken) == 270 && await b.CountItems(cancellationToken: cancellationToken) == 1, "Partition enumeration or pagination failed.");
            string?[] bulk = await a.GetItems(["1", "missing", "1"], cancellationToken: cancellationToken);
            Check(bulk[0] == bulk[2] && bulk[0] is not null && bulk[1] is null, "Bulk lookup failed.");
            await a.EnsureIndex("score_value", cancellationToken: cancellationToken);
            var page = await a.FindRangeByIndex<NativeDocument>("score_value", 10, 20, descending: true, skip: 1, take: 3, cancellationToken: cancellationToken);
            Check(page.Items.Select(x => x.Score).SequenceEqual([19, 18, 17]), "Native range ordering failed.");
            Check(await a.CountRangeByIndex("score_value", 0, 269, cancellationToken: cancellationToken) == 270, "Bookmark count pagination failed.");
            Check(await a.CountByIndex("score_value", 1, cancellationToken: cancellationToken) == 1 && await a.ExistsByIndex("score_value", 1, cancellationToken: cancellationToken), "Equality queries failed.");
            ILibrarianContainer competing = await second.GetContainer("items", "org-a", cancellationToken: cancellationToken);
            LibrarianItem<string> original = (await a.GetItemWithVersion("1", cancellationToken: cancellationToken))!;
            var updates = await Task.WhenAll(a.UpdateItemIfVersion("1", original.Document, original.Version, cancellationToken: cancellationToken).AsTask(),
                competing.UpdateItemIfVersion("1", original.Document, original.Version, cancellationToken: cancellationToken).AsTask());
            Check((updates[0] is null) != (updates[1] is null), "Revision checks did not coordinate across instances.");
            Check(!await a.DeleteItemIfVersion("1", original.Version, cancellationToken: cancellationToken), "Stale deletion succeeded.");
            await database.UnloadContainer("items", cancellationToken: cancellationToken);
            a = await database.GetContainer("items", "org-a", cancellationToken: cancellationToken);
            Check(await a.CountItems(cancellationToken: cancellationToken) == 270, "Unload deleted remote data.");
            await a.DeleteAllItems(cancellationToken: cancellationToken);
            Check(await a.CountItems(cancellationToken: cancellationToken) == 0 && await (await database.GetContainer("items", "org-b", cancellationToken: cancellationToken)).CountItems(cancellationToken: cancellationToken) == 1, "Clear crossed partitions.");
        }
        finally
        {
            HttpClient client = await httpClientCache.Get("couchdb-integration-cleanup");
            using var request = new HttpRequestMessage(HttpMethod.Delete, options.Endpoint.AbsoluteUri.TrimEnd('/') + "/" + physical);
            if (options.Username is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(options.Username + ":" + options.Password)));
            using HttpResponseMessage response = await client.SendAsync(request);
            if (response.StatusCode != System.Net.HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
        }
    }
}
