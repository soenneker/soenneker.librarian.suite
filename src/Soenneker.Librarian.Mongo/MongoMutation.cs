namespace Soenneker.Librarian.Mongo;

public sealed record MongoMutation(string Id, MongoDocument? Document);
