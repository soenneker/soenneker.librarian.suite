using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Soenneker.Librarian.Mongo;

public sealed class MongoDocument
{
    public required string Id { get; init; }
    public required string Container { get; init; }
    public required string OriginalId { get; init; }
    public required string Json { get; init; }
    public Dictionary<string, string> Values { get; init; } = new(StringComparer.Ordinal);

    public static string Field(string path) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    public static string Address(string container, string id) => MongoIndexValue.Hex(container) + ":" + MongoIndexValue.Hex(id.ToUpperInvariant());

    public static MongoDocument Create(string container, string id, string json, IEnumerable<string> indexes)
    {
        var result = new MongoDocument { Id = Address(container, id), Container = container, OriginalId = id, Json = json };
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException)
        {
            foreach (string _ in indexes) throw new ArgumentException("Indexed documents must contain valid JSON.", nameof(json));
            return result;
        }
        using (document)
        {
            foreach (string path in indexes) MongoIndexValue.Read(document.RootElement, path);
            AddScalars(document.RootElement, "", MongoIndexValue.Hex(id.ToUpperInvariant()), result.Values);
        }
        return result;
    }

    internal static void ValidateIndex(string json, string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            MongoIndexValue.Read(document.RootElement, path);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Indexed documents must contain valid JSON.", nameof(json), exception);
        }
    }

    private static void AddScalars(JsonElement element, string prefix, string id, Dictionary<string, string> values)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Name.Length == 0 || property.Name.Contains('.', StringComparison.Ordinal)) continue;
            string path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
            // Match JsonElement.TryGetProperty's last-property-wins semantics for duplicate JSON names.
            JsonElement value = element.GetProperty(property.Name);
            if (value.ValueKind == JsonValueKind.Object) AddScalars(value, path, id, values);
            else
            {
                try { values[Field(path)] = MongoIndexValue.Encode(value) + "!" + id; }
                catch (ArgumentException) { /* Non-scalars remain valid until this path is indexed. */ }
            }
        }
    }
}
