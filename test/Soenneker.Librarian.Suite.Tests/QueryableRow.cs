using System.Text.Json.Serialization;
using System.Threading;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class QueryableRow
{
    public static int Created;
    public QueryableRow() => Interlocked.Increment(ref Created);
    [JsonPropertyName("score_value")]
    public int Score { get; set; } = 7;
    public string? Name { get; set; }
    public int Computed => Score % 2;
}
