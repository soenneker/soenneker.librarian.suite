using Soenneker.Documents.Document;

namespace Soenneker.Librarian.Suite.Tests;

public sealed class RevisionDocument : Document
{
    public long Revision { get; set; }
    public long OtherRevision { get; set; }
    public string Name { get; set; } = "";
}
