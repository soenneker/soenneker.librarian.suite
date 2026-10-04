using System;
using System.Reflection;
using System.Text.Json;
using MongoDB.Bson;
using Soenneker.Librarian.Mongo;
using Soenneker.Librarian.Abstractions.Serialization;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class NativeJsonTests
{
    [Test]
    public void Bson_conversion_preserves_nested_metadata_and_exact_scalar_values_without_mutating_input()
    {
        var value = new BsonDocument
        {
            { "_id", "metadata" }, { "_librarianVersion", "version" },
            { "nested", new BsonDocument { { "_id", "user-data" }, { "_librarianVersion", "nested-version" } } },
            { "values", new BsonArray { BsonNull.Value, true, "é😀\"", long.MaxValue, new BsonDecimal128(decimal.MaxValue), 1.25d } }
        };
        var original = value.DeepClone();
        Type converter = typeof(MongoLibrarianDatabase).Assembly.GetType("Soenneker.Librarian.Mongo.MongoJsonValue")!;
        MethodInfo method = converter.GetMethod("ToJson", BindingFlags.NonPublic | BindingFlags.Static)!;
        string json = (string)method.Invoke(null, [value, true])!;
        using JsonDocument result = JsonDocument.Parse(json);
        JsonElement root = result.RootElement;
        if (root.TryGetProperty("_id", out _) || root.TryGetProperty("_librarianVersion", out _) ||
            root.GetProperty("nested").GetProperty("_id").GetString() != "user-data" ||
            root.GetProperty("values")[3].GetInt64() != long.MaxValue ||
            root.GetProperty("values")[4].GetDecimal() != decimal.MaxValue || !value.Equals(original))
            throw new Exception("BSON conversion lost data or changed its input.");
        if (root.GetProperty("values")[0].ValueKind != JsonValueKind.Null ||
            !root.GetProperty("values")[1].GetBoolean() || root.GetProperty("values")[2].GetString() != "é😀\"" ||
            root.GetProperty("values")[5].GetDouble() != 1.25) throw new Exception("Scalar conversion changed.");
    }

    [Test]
    public void Validation_only_path_rejects_the_same_reserved_fields_and_identities()
    {
        const string valid = "{\"id\":\"one\",\"partitionKey\":\"org\",\"nested\":{\"_id\":1}}";
        LibrarianDocumentJson.Validate("one", valid, "org");
        foreach (string invalid in new[] { "{}", valid.Replace("org", "other"), valid.Replace("\"nested\"", "\"_etag\"") })
        {
            try { LibrarianDocumentJson.Validate("one", invalid, "org"); throw new Exception("Invalid document accepted."); }
            catch (ArgumentException) { }
        }
    }
}
