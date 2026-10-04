namespace Soenneker.Librarian.Core.Indexes;

internal struct CandidateEnumerator(DocumentIndexNode? next, int remaining, bool descending)
{
    public string Current { get; private set; } = null!;
    public readonly CandidateEnumerator GetEnumerator() => this;

    public bool MoveNext()
    {
        if (remaining <= 0) return false;
        Current = next!.Id;
        next = descending ? next.Previous : next.Next;
        remaining--;
        return true;
    }
}
