namespace Soenneker.Librarian.Suite.Tests;

public sealed class AuditRow
{
    public static int Created;
    public AuditRow() => Created++;
    public int Score { get; set; }
    public string Name { get; set; } = "";
}
