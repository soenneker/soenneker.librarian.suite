namespace Soenneker.Librarian.Suite.Tests;

public sealed class PlannerRow
{
    public static int Created;
    public PlannerRow() => Created++;
    public int Score { get; set; }
    public string Status { get; set; } = "other";
    public string Name { get; set; } = "no";
    public bool Active { get; set; }
}
