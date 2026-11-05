using System;

namespace Soenneker.Librarian.Abstractions;

/// <summary>The selected application revision no longer matches. The rejected mutation was not applied.</summary>
public sealed class LibrarianRevisionConflictException(long expectedRevision, long actualRevision)
    : InvalidOperationException($"Expected revision {expectedRevision}, but the current revision is {actualRevision}.")
{
    public long ExpectedRevision { get; } = expectedRevision;
    public long ActualRevision { get; } = actualRevision;
}
