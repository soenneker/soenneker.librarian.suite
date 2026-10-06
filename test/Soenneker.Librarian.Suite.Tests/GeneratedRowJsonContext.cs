using System.Text.Json.Serialization;

namespace Soenneker.Librarian.Suite.Tests;

[JsonSerializable(typeof(GeneratedRow))]
[JsonSerializable(typeof(AutomaticQueryRow))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class GeneratedRowJsonContext : JsonSerializerContext;
