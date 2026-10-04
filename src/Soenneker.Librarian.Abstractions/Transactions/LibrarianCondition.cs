namespace Soenneker.Librarian.Abstractions.Transactions;

/// <summary>Requires the current raw document text to equal an expected value. Native MongoDB/Cosmos batches use versioned writes instead.</summary>
/// <param name="Container">Case-sensitive container name.</param>
/// <param name="Id">Document ID; native providers use case-sensitive Document identities.</param>
/// <param name="ExpectedValue">Expected raw document text, or null to require that the document does not exist.</param>
public sealed record LibrarianCondition(string Container, string Id, string? ExpectedValue);
