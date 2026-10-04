using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;

namespace Soenneker.Librarian.Mongo;

internal static class MongoJsonValue
{
    internal static BsonValue FromJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => Document(value),
        JsonValueKind.Array => new BsonArray(value.EnumerateArray().Select(FromJson)),
        JsonValueKind.String => new BsonString(value.GetString()!),
        JsonValueKind.Number when value.TryGetInt64(out long number) => new BsonInt64(number),
        JsonValueKind.Number when value.TryGetDecimal(out decimal number) => new BsonDecimal128(number),
        JsonValueKind.Number => new BsonDouble(value.GetDouble()),
        JsonValueKind.True => BsonBoolean.True,
        JsonValueKind.False => BsonBoolean.False,
        _ => BsonNull.Value
    };

    private static BsonDocument Document(JsonElement value)
    {
        var document = new BsonDocument();
        foreach (JsonProperty property in value.EnumerateObject()) document[property.Name] = FromJson(property.Value);
        return document;
    }

    internal static JsonNode? ToJson(BsonValue value) => value.BsonType switch
    {
        BsonType.Document => new JsonObject(value.AsBsonDocument.Select(element =>
            new System.Collections.Generic.KeyValuePair<string, JsonNode?>(element.Name, ToJson(element.Value)))),
        BsonType.Array => new JsonArray(value.AsBsonArray.Select(ToJson).ToArray()),
        BsonType.String => JsonValue.Create(value.AsString),
        BsonType.Boolean => JsonValue.Create(value.AsBoolean),
        BsonType.Int32 => JsonValue.Create(value.AsInt32),
        BsonType.Int64 => JsonValue.Create(value.AsInt64),
        BsonType.Double => JsonValue.Create(value.AsDouble),
        BsonType.Decimal128 => JsonValue.Create(Decimal128.ToDecimal(value.AsDecimal128)),
        BsonType.Null => null,
        _ => throw new NotSupportedException($"BSON {value.BsonType} is not a Librarian JSON value.")
    };
}
