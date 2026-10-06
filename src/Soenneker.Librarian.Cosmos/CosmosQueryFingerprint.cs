namespace Soenneker.Librarian.Cosmos;
internal sealed record CosmosQueryFingerprint(string scope, string Database, string Container, string? Type, string Sql, CosmosQueryParameter[]? Parameters);
