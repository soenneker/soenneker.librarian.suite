namespace Soenneker.Librarian.Abstractions.Transactions;

/// <summary>Sets or deletes a document as part of an atomic batch.</summary>
/// <param name="Container">Case-sensitive container name.</param>
/// <param name="Id">Document ID; native providers use case-sensitive Document identities.</param>
/// <param name="Value">New document JSON, or null to delete. Native batch deletes require an existing document.</param>
/// <param name="ExpectedVersion">Requires this version for a replacement or deletion. Supported by MongoDB and Cosmos.</param>
/// <param name="CreateOnly">Inserts only if absent. Cannot be combined with a version or deletion. Supported by MongoDB and Cosmos.</param>
public sealed record LibrarianWrite(string Container, string Id, string? Value, string? ExpectedVersion = null, bool CreateOnly = false);
