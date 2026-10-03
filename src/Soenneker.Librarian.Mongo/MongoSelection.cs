namespace Soenneker.Librarian.Mongo;

public sealed record MongoSelection(string Container, MongoQueryFilter Filter, string? Order = null, bool Descending = false,
    int Skip = 0, int Take = int.MaxValue);
