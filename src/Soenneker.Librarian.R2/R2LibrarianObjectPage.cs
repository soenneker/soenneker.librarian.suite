namespace Soenneker.Librarian.R2;

/// <summary>A page of keys and an opaque continuation cursor; null marks the last page.</summary>
public sealed record R2LibrarianObjectPage(string[] Keys, string? Cursor);
