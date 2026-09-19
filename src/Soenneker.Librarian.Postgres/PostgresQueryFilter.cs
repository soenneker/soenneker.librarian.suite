namespace Soenneker.Librarian.Postgres;

internal sealed record PostgresQueryFilter(string Operation, string? Path = null, string Comparison = "=", string? Value = null,
    PostgresQueryFilter? Left = null, PostgresQueryFilter? Right = null, string[]? Values = null,
    PostgresScalar? ScalarLeft = null, PostgresScalar? ScalarRight = null);
