using System;
using System.IO;
using System.Text.Json;
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
        using var buffer = new CosmosJsonBuffer();
        if (version is null) buffer.Write(json);
        else
        {
            using JsonDocument document = JsonDocument.Parse(json);
            WriteDocument(buffer, document.RootElement, version, stripMetadata: false);
        }
        using MemoryStream stream = buffer.OpenRead();
        return Serializer.FromStream<T>(stream);
    }

    private static void WriteDocument(CosmosJsonBuffer buffer, JsonElement document, string? version = null, bool stripMetadata = true)
    {
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        foreach (JsonProperty property in document.EnumerateObject())
        {
            if (property.NameEquals("_etag") && (stripMetadata || version is not null)) continue;
            if (stripMetadata && (property.NameEquals("_rid") || property.NameEquals("_self") ||
                property.NameEquals("_attachments") || property.NameEquals("_ts"))) continue;
            property.WriteTo(writer);
        }
        if (version is not null) writer.WriteString("_etag", version);
        writer.WriteEndObject();
    }

}
