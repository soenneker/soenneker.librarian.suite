namespace Soenneker.Librarian.Mongo;

internal sealed class MongoQueryDocument<T>
{
    public T Body { get; set; } = default!;
}
