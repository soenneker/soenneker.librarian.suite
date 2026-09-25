using System.Collections.Generic;
using System.Text.Json.Serialization;
using Soenneker.Dtos.IdValuePair;

namespace Soenneker.Librarian.Core;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Dictionary<string, List<IdValuePair>>), TypeInfoPropertyName = "Database")]
internal partial class SnapshotJsonContext : JsonSerializerContext;
