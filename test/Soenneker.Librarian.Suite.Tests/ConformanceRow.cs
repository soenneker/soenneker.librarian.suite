namespace Soenneker.Librarian.Suite.Tests;

public sealed class ConformanceRow
{
    public static readonly ReadCounter Created = new();
    public ConformanceRow() => Created.Value++;
    public int Score { get; set; }
    public decimal Amount { get; set; }
    public string? Name { get; set; }
    public bool Active { get; set; }
}
