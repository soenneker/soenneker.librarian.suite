using System.Collections.Generic;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

// All keys are explicit KEYS arguments and share the database's Redis Cluster hash tag.
internal sealed class RedisBatchCommands
{
    private const string Script = """
        local p = 1
        local versions = tonumber(ARGV[p])
        p = p + 1
        for i = 1, versions do
            local actual = redis.call('GET', KEYS[tonumber(ARGV[p])])
            if ARGV[p + 1] == '0' then
                if actual ~= false then return 0 end
            elseif actual ~= ARGV[p + 2] then
                return 0
            end
            p = p + 3
        end
        while p <= #ARGV do
            local op = ARGV[p]
            p = p + 1
            if op == 'put' then
                local document, ids = KEYS[tonumber(ARGV[p])], KEYS[tonumber(ARGV[p + 1])]
                redis.call('HSET', document, 'json', ARGV[p + 4])
                redis.call('HSETNX', document, 'id', ARGV[p + 3])
                redis.call('SADD', ids, ARGV[p + 2])
                p = p + 5
            elseif op == 'delete' then
                redis.call('DEL', KEYS[tonumber(ARGV[p])])
                redis.call('SREM', KEYS[tonumber(ARGV[p + 1])], ARGV[p + 2])
                p = p + 3
            elseif op == 'index' then
                local document = KEYS[tonumber(ARGV[p])]
                local index, distinct, present = KEYS[tonumber(ARGV[p + 1])], KEYS[tonumber(ARGV[p + 2])], KEYS[tonumber(ARGV[p + 3])]
                local field, sortField, id, old, next = ARGV[p + 6], ARGV[p + 7], ARGV[p + 8], ARGV[p + 9], ARGV[p + 10]
                if old ~= '' then
                    local bucket = KEYS[tonumber(ARGV[p + 4])]
                    redis.call('ZREM', index, old .. '!' .. id)
                    redis.call('SREM', bucket, id)
                    if redis.call('SCARD', bucket) == 0 then redis.call('ZREM', distinct, old) end
                end
                if next == '' then
                    redis.call('HDEL', document, field, sortField)
                    redis.call('SREM', present, id)
                else
                    local member = next .. '!' .. id
                    redis.call('HSET', document, field, next, sortField, member)
                    redis.call('ZADD', index, 0, member)
                    redis.call('ZADD', distinct, 0, next)
                    redis.call('SADD', KEYS[tonumber(ARGV[p + 5])], id)
                    redis.call('SADD', present, id)
                end
                p = p + 11
            elseif op == 'version' then
                redis.call('INCR', KEYS[tonumber(ARGV[p])])
                p = p + 1
            end
        end
        return 1
        """;

    private readonly List<RedisKey> _keys = [];
    private readonly Dictionary<RedisKey, int> _keyIndexes = [];
    private readonly List<RedisValue> _versions = [];
    private readonly List<RedisValue> _commands = [];

    private int Key(RedisKey key)
    {
        if (_keyIndexes.TryGetValue(key, out int index)) return index;
        index = _keys.Count + 1;
        _keys.Add(key);
        _keyIndexes.Add(key, index);
        return index;
    }

    internal void Version(RedisKey key, RedisValue expected, bool changed)
    {
        int index = Key(key);
        _versions.Add(index);
        _versions.Add(expected.IsNull ? "0" : "1");
        _versions.Add(expected.IsNull ? "" : expected);
        if (changed) { _commands.Add("version"); _commands.Add(index); }
    }

    internal void Write(RedisKey document, RedisKey ids, string normalized, string id, string? value)
    {
        _commands.Add(value is null ? "delete" : "put");
        _commands.Add(Key(document));
        _commands.Add(Key(ids));
        _commands.Add(normalized);
        if (value is not null) { _commands.Add(id); _commands.Add(value); }
    }

    internal void Index(RedisKey document, RedisKey index, RedisKey distinct, RedisKey present,
        RedisKey oldBucket, RedisKey newBucket, string field, string sortField, string id, string? old, string? next)
    {
        _commands.Add("index");
        _commands.Add(Key(document));
        _commands.Add(Key(index));
        _commands.Add(Key(distinct));
        _commands.Add(Key(present));
        _commands.Add(old is null ? 0 : Key(oldBucket));
        _commands.Add(next is null ? 0 : Key(newBucket));
        _commands.Add(field);
        _commands.Add(sortField);
        _commands.Add(id);
        _commands.Add(old ?? "");
        _commands.Add(next ?? "");
    }

    internal Task<RedisResult> Execute(IDatabase store)
    {
        var arguments = new RedisValue[1 + _versions.Count + _commands.Count];
        arguments[0] = _versions.Count / 3;
        _versions.CopyTo(arguments, 1);
        _commands.CopyTo(arguments, 1 + _versions.Count);
        return store.ScriptEvaluateAsync(Script, _keys.ToArray(), arguments);
    }
}
