namespace Soenneker.Librarian.Abstractions.Transactions;

/// <summary>Requires the current document to equal an expected raw value using ordinal comparison.</summary>
/// <param name="Container">Case-sensitive container name.</param>
/// <param name="Id">Case-insensitive document ID.</param>
/// <param name="ExpectedValue">Exact stored text, or null to require that the document does not exist.</param>
public sealed record LibrarianCondition(string Container, string Id, string? ExpectedValue);
