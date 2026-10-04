using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class MongoLinqRow : Soenneker.Documents.Document.Document
{
    [JsonPropertyName("score_value")]
    public int Score { get; set; }
    public string Name { get; set; } = "";
    public decimal Amount { get; set; }
    public List<int> Scores { get; set; } = [];
    public MongoLinqDetails Details { get; set; } = new();
    public RedisStatus Status { get; set; }
    public int? Optional { get; set; }
    public Dictionary<string, int> Metrics { get; set; } = [];
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public int StringNumber { get; set; }
}
