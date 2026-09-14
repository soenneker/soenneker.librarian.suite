namespace Soenneker.Librarian.Redis;

internal sealed record RedisQueryFilter(string Operation, string? Path = null, string Minimum = "-", string Maximum = "+",
    RedisQueryFilter? Left = null, RedisQueryFilter? Right = null);
