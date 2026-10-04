namespace Soenneker.Librarian.Suite.Tests;

public sealed class PostgresRow
{
    public static readonly ReadCounter Reads = new();
    private decimal _amount;
    public decimal Amount { get => _amount; set { _amount = value; Reads.Value++; } }
    public string? Name { get; set; }
    public bool Active { get; set; }
}
