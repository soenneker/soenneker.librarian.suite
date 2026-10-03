namespace Soenneker.Librarian.Mongo;

public sealed record MongoQueryFilter(string Operation, string? Path = null, string Minimum = "-", string Maximum = "+",
    MongoQueryFilter? Left = null, MongoQueryFilter? Right = null, string[]? Values = null);
