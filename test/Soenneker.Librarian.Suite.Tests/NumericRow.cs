using System.Text.Json.Serialization;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class NumericRow
{
    [JsonPropertyName("integer_value")]
    public int Number { get; set; }
    public decimal Amount { get; set; }
    public int? Optional { get; set; }
    public double Fraction { get; set; }
}
