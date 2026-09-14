namespace Soenneker.Librarian.Core.Indexes;

internal sealed class DocumentIndexNode(IndexKey key, string id, uint priority)
{
    internal IndexKey Key = key;
    internal readonly string Id = id;
    internal readonly uint Priority = priority;
    internal DocumentIndexNode? Left;
    internal DocumentIndexNode? Right;
    internal DocumentIndexNode? Previous;
    internal DocumentIndexNode? Next;
    internal int Size = 1;
}
