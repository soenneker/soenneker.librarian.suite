namespace Soenneker.Librarian.Abstractions;

/// <summary>A document and its opaque version. Versions are valid only for the document and partition read.</summary>
public sealed record LibrarianItem<T>(T Document, string Version);
