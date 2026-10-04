using System;
using System.Buffers;
using System.Linq;
using System.Text;
using System.Text.Json;
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

    internal static string ToJson(BsonValue value, bool excludeMetadata = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) Write(writer, value, excludeMetadata);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void Write(Utf8JsonWriter writer, BsonValue value, bool excludeMetadata = false)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                writer.WriteStartObject();
                foreach (BsonElement element in value.AsBsonDocument)
                {
                    if (excludeMetadata && element.Name is "_id" or "_librarianVersion") continue;
                    writer.WritePropertyName(element.Name);
                    Write(writer, element.Value);
                }
                writer.WriteEndObject();
                break;
            case BsonType.Array:
                writer.WriteStartArray();
                foreach (BsonValue item in value.AsBsonArray) Write(writer, item);
                writer.WriteEndArray();
                break;
            case BsonType.String: writer.WriteStringValue(value.AsString); break;
            case BsonType.Boolean: writer.WriteBooleanValue(value.AsBoolean); break;
            case BsonType.Int32: writer.WriteNumberValue(value.AsInt32); break;
            case BsonType.Int64: writer.WriteNumberValue(value.AsInt64); break;
            case BsonType.Double: writer.WriteNumberValue(value.AsDouble); break;
            case BsonType.Decimal128: writer.WriteNumberValue(Decimal128.ToDecimal(value.AsDecimal128)); break;
            case BsonType.Null: writer.WriteNullValue(); break;
            default: throw new NotSupportedException($"BSON {value.BsonType} is not a Librarian JSON value.");
        }
    }
}
