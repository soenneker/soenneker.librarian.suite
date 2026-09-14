using Soenneker.Documents.Document;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class IndexRow : Document
{
    public string Status { get; set; } = "";
    public int Score { get; set; }
}
