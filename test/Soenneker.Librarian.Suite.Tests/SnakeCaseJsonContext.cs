using System.Text.Json.Serialization;

namespace Soenneker.Librarian.Suite.Tests;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SnakeCaseRow))]
internal partial class SnakeCaseJsonContext : JsonSerializerContext;
