namespace Soenneker.Librarian.Suite.Tests;

public sealed class FieldRow
{
    public static int Adjustment;
    public int Score { get => field + Adjustment; set; }
}
