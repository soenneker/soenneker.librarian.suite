using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soenneker.Librarian.Cosmos;

internal sealed class CosmosDocument
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("partitionKey")] public required string PartitionKey { get; init; }
    [JsonPropertyName("containerName")] public required string ContainerName { get; init; }
    [JsonPropertyName("originalId")] public required string OriginalId { get; init; }
    [JsonPropertyName("sortId")] public required string SortId { get; init; }
    [JsonPropertyName("rawJson")] public required string RawJson { get; init; }
    [JsonPropertyName("body")] public JsonElement? Body { get; init; }

    internal static string IdFor(string container, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { container, id.ToUpperInvariant() }))));
    }
    internal static CosmosDocument Create(string key, string container, string id, string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonElement? body = null;
        try { using JsonDocument parsed = JsonDocument.Parse(json); body = parsed.RootElement.Clone(); }
        catch (JsonException) { /* Raw Librarian strings remain readable, but are not typed query documents. */ }
        return new CosmosDocument
        {
            Id = IdFor(container, id), PartitionKey = key, ContainerName = container, OriginalId = id,
            SortId = string.Concat(System.Linq.Enumerable.Select(id.ToUpperInvariant(), c => ((int)c).ToString("X4"))), RawJson = json, Body = body
        };
    }
    internal MemoryStream ToStream() => new(JsonSerializer.SerializeToUtf8Bytes(this));
    internal static CosmosDocument Read(JsonElement element) => element.Deserialize<CosmosDocument>() ?? throw new InvalidDataException("Invalid Librarian Cosmos document.");
}
