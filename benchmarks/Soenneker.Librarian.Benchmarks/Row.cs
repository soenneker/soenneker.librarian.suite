public sealed class Row
{
    [SQLite.PrimaryKey]
    public string Id { get; set; } = "";
    public string Group { get; set; } = "";
    public int Score { get; set; }
    public string Payload { get; set; } = "";
}

