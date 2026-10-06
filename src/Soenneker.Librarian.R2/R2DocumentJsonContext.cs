using System.Text.Json.Serialization;

namespace Soenneker.Librarian.R2;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(R2DocumentEnvelope))]
[JsonSerializable(typeof(R2LibrarianObjectPage))]
internal partial class R2DocumentJsonContext : JsonSerializerContext;
