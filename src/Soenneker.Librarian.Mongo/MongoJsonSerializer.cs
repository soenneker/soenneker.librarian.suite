using Soenneker.Utils.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Soenneker.Librarian.Mongo;

internal class MongoJsonSerializer<T>(JsonTypeInfo typeInfo) : SerializerBase<T>, IHasRepresentationSerializer
{
    protected JsonTypeInfo<T> TypeInfo { get; } = (JsonTypeInfo<T>)typeInfo;
    public BsonType Representation { get; } = RepresentationFor(typeInfo);

    private static BsonType RepresentationFor(JsonTypeInfo info)
    {
        if (info.Kind == JsonTypeInfoKind.Object) return BsonType.Document;
        object? sample = typeof(T) == typeof(string) ? "" : default(T);
        JsonElement value = JsonUtil.SerializeToElement((T)sample!, (JsonTypeInfo<T>)info);
        return value.ValueKind switch
        {
            JsonValueKind.String => BsonType.String,
            JsonValueKind.True or JsonValueKind.False => BsonType.Boolean,
            JsonValueKind.Number when typeof(T) == typeof(decimal) => BsonType.Decimal128,
            JsonValueKind.Number when typeof(T) == typeof(double) || typeof(T) == typeof(float) => BsonType.Double,
            JsonValueKind.Number => BsonType.Int64,
            _ => BsonType.Undefined
        };
    }
    public override T Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        BsonValue value = BsonValueSerializer.Instance.Deserialize(context);
        if (value is BsonDocument document && typeof(Soenneker.Documents.Document.Document).IsAssignableFrom(typeof(T)))
        {
            document.Remove("_id");
            document.Remove("_librarianVersion");
        }
        return JsonUtil.Deserialize(MongoJsonValue.ToJson(value)?.ToJsonString() ?? "null", TypeInfo)!;
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, T value) =>
        BsonValueSerializer.Instance.Serialize(context, MongoJsonValue.FromJson(JsonUtil.SerializeToElement(value, TypeInfo)));
}
