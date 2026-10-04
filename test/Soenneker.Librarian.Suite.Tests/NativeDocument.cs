using System.Text.Json.Serialization;
using Soenneker.Documents.Document;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class NativeDocument : Document
{
    [JsonPropertyName("score_value")]
    public int Score { get; set; }
    public string? Name { get; set; }
}
