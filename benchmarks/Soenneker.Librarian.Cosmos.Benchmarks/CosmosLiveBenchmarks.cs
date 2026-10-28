using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Soenneker.Librarian.Cosmos;

internal static class CosmosLiveBenchmarks
{
    public static async Task Run(bool productionSerializer)
    {
        string connection = Environment.GetEnvironmentVariable("LIBRARIAN_TEST_COSMOS")
            ?? throw new InvalidOperationException("Set LIBRARIAN_TEST_COSMOS to a local emulator connection string.");
        var settings = new DbConnectionStringBuilder { ConnectionString = connection };
        if (!new Uri((string)settings["AccountEndpoint"]).IsLoopback) throw new InvalidOperationException("This benchmark requires a local emulator.");
        await using var streams = new Soenneker.Utils.MemoryStream.MemoryStreamUtil();
        using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        var options = new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            HttpClientFactory = () => http
        };
        if (productionSerializer) options.Serializer = new Soenneker.Cosmos.Serializer.CosmosSystemTextJsonSerializer(streams);
        else options.UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using var client = new CosmosClient(connection, options);
        Console.WriteLine(productionSerializer ? "Serializer: Soenneker" : "Serializer: SDK System.Text.Json");
        string name = "librarian-benchmark-" + Guid.NewGuid();
        Database database = await client.CreateDatabaseAsync(name, 10000);
        try
        {
            Container sdk = await database.CreateContainerAsync("items", "/partitionKey");
            await using var librarian = new CosmosLibrarianDatabase(sdk);
            var items = await librarian.GetCosmosContainer("items");
            Console.WriteLine("Live gateway reads; median of 5 alternating rounds; 100 paced reads per round. Allocation includes background SDK activity.");
            Console.WriteLine("Payload,Path,MicrosecondsPerRead,ProcessBytesPerRead");
            foreach (int size in new[] { 4096, 65536 })
            {
                var document = new CosmosBenchmarkDocument { Id = "row", Payload = new string('x', size) };
                await sdk.UpsertItemAsync(document, new PartitionKey("row"));
                Func<Task<int>>[] actions = [
                    async () => (await sdk.ReadItemAsync<CosmosBenchmarkDocument>("row", new PartitionKey("row"))).Resource.Payload.Length,
                    async () => (await items.ReadDocument<CosmosBenchmarkDocument>("row"))!.Document.Payload.Length,
                    async () => { var raw = (await items.GetItemWithVersion("row"))!; return items.DeserializeDocument<CosmosBenchmarkDocument>(raw.Document, raw.Version).Payload.Length; }
                ];
                var times = new double[3, 5];
                var bytes = new double[3, 5];
                foreach (var action in actions) for (int i = 0; i < 30; i++) if (await action() != size) throw new InvalidOperationException("Read mismatch.");
                for (int round = 0; round < 5; round++)
                    for (int turn = 0; turn < 3; turn++)
                    {
                        int index = (round + turn) % 3;
                        // Pace outside the measured interval to avoid saturating a hot emulator partition.
                        for (int i = 0; i < 100; i++)
                        {
                            await Task.Delay(5);
                            long allocated = GC.GetTotalAllocatedBytes(true);
                            long start = Stopwatch.GetTimestamp();
                            if (await actions[index]() != size) throw new InvalidOperationException("Read mismatch.");
                            times[index, round] += Stopwatch.GetElapsedTime(start).TotalMicroseconds / 100;
                            bytes[index, round] += (GC.GetTotalAllocatedBytes(true) - allocated) / 100.0;
                        }
                    }
                string[] labels = ["DirectSDK", "NativeLibrarian", "PortableJsonPipeline"];
                for (int index = 0; index < 3; index++)
                    Console.WriteLine($"{size},{labels[index]},{Enumerable.Range(0, 5).Select(r => times[index, r]).Order().ElementAt(2):F1},{Enumerable.Range(0, 5).Select(r => bytes[index, r]).Order().ElementAt(2):F0}");
            }
            await CosmosPagingLiveBenchmarks.Run(sdk, items);
        }
        finally { await database.DeleteAsync(); }
    }
}
