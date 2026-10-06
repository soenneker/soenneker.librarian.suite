using System.Text.Json;
namespace Soenneker.Librarian.Cosmos;
internal sealed record CosmosQueryParameter(string Name, JsonElement Value);
