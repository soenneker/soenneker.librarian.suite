using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SmokeRow))]
[JsonSerializable(typeof(SmokeStatus))]
[JsonSerializable(typeof(SmokeDocument))]
internal partial class SmokeJsonContext : JsonSerializerContext;
