using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;
using Soenneker.Librarian.Cosmos;

internal static class CosmosSerializationBenchmarks
{
    public static async Task Run()
    {
        using var client = new CosmosClient("https://localhost:8081/", Convert.ToBase64String(new byte[64]),
            new CosmosClientOptions { UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) });
        await using var database = new CosmosLibrarianDatabase(client.GetContainer("benchmarks", "rows"));
        ICosmosLibrarianContainer container = await database.GetCosmosContainer("rows");
        CosmosSerializer serializer = client.ClientOptions.Serializer;
        Console.WriteLine("Operation,PayloadBytes,Implementation,MedianMicroseconds,BytesPerOperation");
        foreach (int size in new[] { 128, 4096, 65536 })
        {
            var row = new CosmosBenchmarkDocument { Id = "row", Payload = new string('x', size), Score = 42 };
            string json = container.SerializeDocument(row);
            CosmosBenchmarkDocument BeforeRead()
            {
                JsonNode node = JsonNode.Parse(json)!;
                node["_etag"] = "version";
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(node.ToJsonString()));
                return serializer.FromStream<CosmosBenchmarkDocument>(stream);
            }
            string BeforeWrite()
            {
                using Stream stream = serializer.ToStream(row);
                using JsonDocument document = JsonDocument.Parse(stream);
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    foreach (JsonProperty property in document.RootElement.EnumerateObject())
                        if (!property.NameEquals("_rid") && !property.NameEquals("_self") && !property.NameEquals("_etag") &&
                            !property.NameEquals("_attachments") && !property.NameEquals("_ts")) property.WriteTo(writer);
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.WrittenSpan);
            }
            using JsonDocument beforeJson = JsonDocument.Parse(BeforeWrite());
            using JsonDocument afterJson = JsonDocument.Parse(json);
            if (!JsonElement.DeepEquals(beforeJson.RootElement, afterJson.RootElement) || BeforeRead().Payload != container.DeserializeDocument<CosmosBenchmarkDocument>(json, "version").Payload)
                throw new InvalidOperationException("Serialization results differ.");
            Measure("DeserializeWithETag", size, () => BeforeRead().Payload.Length,
                () => container.DeserializeDocument<CosmosBenchmarkDocument>(json, "version").Payload.Length);
            Measure("Serialize", size, () => BeforeWrite().Length, () => container.SerializeDocument(row).Length);
        }
    }

    internal static void Measure(string operation, int size, Func<int> before, Func<int> after)
    {
        const int rounds = 7, iterations = 2000;
        Func<int>[] actions = [before, after];
        var times = new double[2, rounds];
        var allocations = new long[2, rounds];
        long checksum = 0;
        foreach (Func<int> action in actions)
            for (int i = 0; i < 500; i++) checksum += action();
        for (int round = 0; round < rounds; round++)
            for (int turn = 0; turn < 2; turn++)
            {
                int index = (round + turn) % 2;
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                long bytes = GC.GetAllocatedBytesForCurrentThread();
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < iterations; i++) checksum += actions[index]();
                times[index, round] = Stopwatch.GetElapsedTime(start).TotalMicroseconds / iterations;
                allocations[index, round] = (GC.GetAllocatedBytesForCurrentThread() - bytes) / iterations;
            }
        for (int index = 0; index < 2; index++)
            Console.WriteLine($"{operation},{size},{(index == 0 ? "Before" : "After")},{Enumerable.Range(0, rounds).Select(r => times[index, r]).Order().ElementAt(rounds / 2):F3},{Enumerable.Range(0, rounds).Select(r => allocations[index, r]).Order().ElementAt(rounds / 2)}");
        GC.KeepAlive(checksum);
    }
}
