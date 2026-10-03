using System.Text.Json.Serialization;
using Newtonsoft.Json;

namespace Soenneker.Librarian.Cosmos;

internal sealed class CosmosQueryDocument<T>
{
    [JsonPropertyName("containerName"), JsonProperty("containerName")]
    public string ContainerName { get; set; } = "";
    [JsonPropertyName("body"), JsonProperty("body")]
    public T Body { get; set; } = default!;
}
