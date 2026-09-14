namespace Soenneker.Librarian.Suite.Tests;

public sealed class RedisRow
{
    public RedisStatus Status { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("display_name")]
    public string DisplayName { get; set; } = "";
    public decimal Amount { get; set; }
    public string Name { get; set; } = "";
    public bool Active { get; set; }
}
