using System.Text.Json.Serialization;
namespace Soenneker.Librarian.Cosmos;
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CosmosContinuation))]
[JsonSerializable(typeof(CosmosQueryFingerprint))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(string))]
internal partial class CosmosInternalJsonContext : JsonSerializerContext;
