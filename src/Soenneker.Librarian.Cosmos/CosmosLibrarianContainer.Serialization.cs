using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;

namespace Soenneker.Librarian.Cosmos;

internal sealed partial class CosmosLibrarianContainer
{
    private CosmosSerializer Serializer => store.Database.Client.ClientOptions.Serializer
        ?? throw new InvalidOperationException("Typed repository operations require an explicitly configured Cosmos serializer.");

    public string SerializeDocument<T>(T document)
    {
        Check();
        using Stream stream = Serializer.ToStream(document);
        using JsonDocument json = JsonDocument.Parse(stream);
        return Json(json.RootElement);
    }

    public T DeserializeDocument<T>(string json, string? version = null)
    {
        Check();
        if (version is not null)
        {
            JsonNode document = JsonNode.Parse(json)!;
            document["_etag"] = version;
            json = document.ToJsonString();
        }
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return Serializer.FromStream<T>(stream);
    }
}
