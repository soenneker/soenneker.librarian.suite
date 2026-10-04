using Soenneker.Utils.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Suite.Tests;

internal static class NativeDocumentJson
{
    internal static string Create(string id, string json = "{}", string? partition = null)
    {
        (string Id, string Partition) address = LibrarianDocumentJson.Address(id, partition);
        JsonObject document = JsonNode.Parse(json)!.AsObject();
        document["id"] = address.Id;
        document["partitionKey"] = address.Partition;
        return document.ToJsonString();
    }
    internal static int Score(string json) => JsonUtil.Deserialize(json, TestJsonContext.Default.NativeDocument)!.Score;
}
