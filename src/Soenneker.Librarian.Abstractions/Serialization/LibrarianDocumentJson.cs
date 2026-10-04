using System;
using System.Text.Json;

namespace Soenneker.Librarian.Abstractions.Serialization;

/// <summary>Validates native document JSON and resolves the composite identity used by Document.</summary>
public static class LibrarianDocumentJson
{
    /// <summary>Resolves a Document.Id, or a document ID within an explicitly selected partition.</summary>
    public static (string Id, string Partition) Address(string id, string? partition = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        int separator = id.LastIndexOf(':');
        string documentId = separator < 0 ? id : id[(separator + 1)..];
        string partitionKey = separator < 0 ? partition ?? id : id[..separator];
        if (string.IsNullOrWhiteSpace(documentId) || string.IsNullOrWhiteSpace(partitionKey) ||
            (partition is not null && partitionKey != partition))
            throw new ArgumentException("The document identity does not match the selected partition.", nameof(id));
        return (documentId, partitionKey);
    }

    /// <summary>Returns the composite identity of a serialized Document.</summary>
    public static string Id(JsonElement document)
    {
        string id = document.GetProperty("id").GetString()!;
        string partition = document.GetProperty("partitionKey").GetString()!;
        return id == partition ? id : partition + ":" + id;
    }

    /// <summary>Requires top-level DocumentId (id) and PartitionKey fields matching the requested identity.</summary>
    public static JsonElement Parse(string id, string json, string? partition = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement root = parsed.RootElement;
        Validate(id, root, partition);
        return root.Clone();
    }

    /// <summary>Validates native document JSON without retaining a copy of its parsed contents.</summary>
    public static void Validate(string id, string json, string? partition = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        using JsonDocument parsed = JsonDocument.Parse(json);
        Validate(id, parsed.RootElement, partition);
    }

    private static void Validate(string id, JsonElement root, string? partition)
    {
        (string documentId, string partitionKey) = Address(id, partition);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out JsonElement actualId) ||
            actualId.ValueKind != JsonValueKind.String || actualId.GetString() != documentId ||
            !root.TryGetProperty("partitionKey", out JsonElement actualPartition) ||
            actualPartition.ValueKind != JsonValueKind.String || actualPartition.GetString() != partitionKey)
            throw new ArgumentException("Native providers require Document JSON with id and partitionKey matching Document.Id and the selected partition.", "json");
        foreach (JsonProperty property in root.EnumerateObject())
            if (property.Name is "_id" or "_librarianVersion" or "_etag" or "_rid" or "_self" or "_attachments" or "_ts")
                throw new ArgumentException($"'{property.Name}' is reserved for provider metadata.", "json");
    }
}
