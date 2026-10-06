using System.Text.Json.Serialization;

namespace Soenneker.Librarian.R2;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(R2DocumentEnvelope))]
internal partial class R2DocumentJsonContext : JsonSerializerContext;
