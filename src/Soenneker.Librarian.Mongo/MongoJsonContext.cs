using System.Text.Json.Serialization;

namespace Soenneker.Librarian.Mongo;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MongoDocument))]
[JsonSerializable(typeof(MongoMetadata))]
public partial class MongoJsonContext : JsonSerializerContext;
