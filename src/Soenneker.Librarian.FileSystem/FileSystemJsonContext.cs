using System.Collections.Generic;
using System.Text.Json.Serialization;
using Soenneker.Dtos.IdValuePair;

namespace Soenneker.Librarian.FileSystem;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Dictionary<string, List<IdValuePair>>), TypeInfoPropertyName = "Database")]
internal partial class FileSystemJsonContext : JsonSerializerContext;
