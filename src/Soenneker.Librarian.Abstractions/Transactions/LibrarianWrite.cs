namespace Soenneker.Librarian.Abstractions.Transactions;

/// <summary>Sets or deletes a document as part of an atomic batch.</summary>
/// <param name="Container">Case-sensitive container name.</param>
/// <param name="Id">Case-insensitive document ID.</param>
/// <param name="Value">New raw document text, or null to delete. Deleting a missing document succeeds.</param>
public sealed record LibrarianWrite(string Container, string Id, string? Value);
