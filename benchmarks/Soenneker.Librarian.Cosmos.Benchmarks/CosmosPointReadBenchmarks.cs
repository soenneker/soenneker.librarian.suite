using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;
using Moq;
using Soenneker.Librarian.Cosmos;

internal static class CosmosPointReadBenchmarks
{
    public static async Task Run()
    {
        using var client = new CosmosClient("https://localhost:8081/", Convert.ToBase64String(new byte[64]),
            new CosmosClientOptions { UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) });
        var sdk = new Mock<Container>();
        sdk.SetupGet(c => c.Id).Returns("rows");
        sdk.SetupGet(c => c.Database).Returns(client.GetDatabase("benchmark"));
        await using var database = new CosmosLibrarianDatabase(sdk.Object);
        var container = await database.GetCosmosContainer("rows");
        foreach (int size in new[] { 128, 4096, 65536 })
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"id\":\"row\",\"partitionKey\":\"row\",\"_rid\":\"system\",\"_etag\":\"version\",\"payload\":\"" + new string('x', size) + "\"}");
            sdk.Setup(c => c.ReadItemStreamAsync("row", new PartitionKey("row"), It.IsAny<ItemRequestOptions>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    var response = new ResponseMessage(HttpStatusCode.OK) { Content = new MemoryStream(body, writable: false) };
                    response.Headers.Add("etag", "version");
                    return response;
                });
            // Original point-read pipeline, including SDK call, metadata removal and ETag restoration.
            async ValueTask<CosmosBenchmarkDocument> Before()
            {
                using ResponseMessage response = await sdk.Object.ReadItemStreamAsync("row", new PartitionKey("row"), new ItemRequestOptions());
                response.EnsureSuccessStatusCode();
                using JsonDocument json = await JsonDocument.ParseAsync(response.Content);
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    foreach (JsonProperty property in json.RootElement.EnumerateObject())
                        if (!property.NameEquals("_rid") && !property.NameEquals("_self") && !property.NameEquals("_etag") &&
                            !property.NameEquals("_attachments") && !property.NameEquals("_ts")) property.WriteTo(writer);
                    writer.WriteEndObject();
                }
                string text = Encoding.UTF8.GetString(buffer.WrittenSpan);
                JsonNode node = JsonNode.Parse(text)!;
                node["_etag"] = response.Headers.ETag;
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(node.ToJsonString()));
                return client.ClientOptions.Serializer.FromStream<CosmosBenchmarkDocument>(stream);
            }
            if ((await Before()).Payload != (await container.ReadDocument<CosmosBenchmarkDocument>("row"))!.Document.Payload)
                throw new InvalidOperationException("Point read results differ.");
            CosmosSerializationBenchmarks.Measure("PointRead-NoNetwork", size,
                () => Before().GetAwaiter().GetResult().Payload.Length,
                () => container.ReadDocument<CosmosBenchmarkDocument>("row").GetAwaiter().GetResult()!.Document.Payload.Length);
            sdk.Invocations.Clear();
        }
    }
}
