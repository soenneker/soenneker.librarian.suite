using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

// One script evaluates the filter and final page against the same server snapshot.
internal sealed class RedisQueryCommands(string prefix)
{
    private const string Script = """
        local temporary = {}
        local count = tonumber(ARGV[8])
        for i = 1, count do temporary[i] = KEYS[tonumber(ARGV[8 + i])] end
        local function execute()
            local orderIndex = tonumber(ARGV[9 + count])
            local selectedIndex = tonumber(ARGV[10 + count])
            local p = 11 + count
            while p <= #ARGV do
                local op, destination = ARGV[p], KEYS[tonumber(ARGV[p + 1])]
                if op == 'range' then
                    local members = redis.call('ZRANGEBYLEX', KEYS[tonumber(ARGV[p + 2])], ARGV[p + 3], ARGV[p + 4])
                    local ids = {}
                    for i = 1, #members do
                        ids[#ids + 1] = string.sub(members[i], string.find(members[i], '!', 1, true) + 1)
                        if #ids == 128 or i == #members then
                            redis.call('SADD', destination, unpack(ids))
                            ids = {}
                        end
                    end
                    p = p + 5
                else
                    local sources = {}
                    local n = tonumber(ARGV[p + 2])
                    for i = 1, n do sources[i] = KEYS[tonumber(ARGV[p + 2 + i])] end
                    redis.call(op, destination, unpack(sources))
                    p = p + 3 + n
                end
                redis.call('EXPIRE', destination, 300)
            end
            local matches = KEYS[tonumber(ARGV[1])]
            local skip, take = tonumber(ARGV[3]), tonumber(ARGV[4])
            if ARGV[2] == '1' then
                return math.min(take, math.max(0, redis.call('SCARD', matches) - skip))
            end
            if take == 0 then return {} end
            local scanOrder = false
            if orderIndex ~= 0 then
                local orderedCount = redis.call('ZCARD', KEYS[orderIndex])
                scanOrder = orderedCount <= 256 or redis.call('SCARD', matches) * 8 >= orderedCount
            end
            if scanOrder then
                local selected = KEYS[selectedIndex]
                local offset, seen, collected = 0, 0, 0
                local command = ARGV[5] == 'DESC' and 'ZREVRANGE' or 'ZRANGE'
                while collected < take do
                    local members = redis.call(command, KEYS[orderIndex], offset, offset + 127)
                    local ids = {}
                    for i = 1, #members do
                        local id = string.sub(members[i], string.find(members[i], '!', 1, true) + 1)
                        if redis.call('SISMEMBER', matches, id) == 1 then
                            seen = seen + 1
                            if seen > skip then
                                ids[#ids + 1] = id
                                collected = collected + 1
                                if collected == take then break end
                            end
                        end
                    end
                    if #ids > 0 then redis.call('SADD', selected, unpack(ids)) end
                    if #members < 128 then break end
                    offset = offset + 128
                end
                matches, skip = selected, 0
            end
            if ARGV[6] == '' then
                return redis.call('SORT', matches, 'LIMIT', skip, take, ARGV[5], 'ALPHA', 'GET', ARGV[7])
            end
            return redis.call('SORT', matches, 'BY', ARGV[6], 'LIMIT', skip, take, ARGV[5], 'ALPHA', 'GET', ARGV[7])
        end
        local ok, result = pcall(execute)
        for i = 1, #temporary do redis.call('DEL', temporary[i]) end
        if not ok then return redis.error_reply(result) end
        return result
        """;

    private readonly List<RedisKey> _keys = [];
    private readonly Dictionary<RedisKey, int> _indexes = [];
    private readonly List<int> _temporary = [];
    private readonly List<RedisValue> _operations = [];
    private readonly string _temporaryPrefix = prefix + "query:" + Guid.NewGuid().ToString("N") + ":";

    private int Key(RedisKey key)
    {
        if (_indexes.TryGetValue(key, out int index)) return index;
        index = _keys.Count + 1;
        _keys.Add(key);
        _indexes.Add(key, index);
        return index;
    }

    internal RedisKey Empty()
    {
        RedisKey key = _temporaryPrefix + _temporary.Count;
        _temporary.Add(Key(key));
        return key;
    }

    internal RedisKey Range(RedisKey index, string minimum, string maximum)
    {
        RedisKey destination = Empty();
        _operations.Add("range");
        _operations.Add(Key(destination));
        _operations.Add(Key(index));
        _operations.Add(minimum);
        _operations.Add(maximum);
        return destination;
    }

    internal RedisKey Combine(string operation, IReadOnlyList<RedisKey> sources)
    {
        if (sources.Count == 0) return Empty();
        if (sources.Count == 1) return sources[0];
        // Bound Lua unpack arguments even for very large membership expressions.
        if (sources.Count > 128)
        {
            var groups = new List<RedisKey>((sources.Count + 127) / 128);
            for (var start = 0; start < sources.Count; start += 128)
            {
                var group = new RedisKey[Math.Min(128, sources.Count - start)];
                for (var i = 0; i < group.Length; i++) group[i] = sources[start + i];
                groups.Add(Combine(operation, group));
            }
            return Combine(operation, groups);
        }
        RedisKey destination = Empty();
        _operations.Add(operation);
        _operations.Add(Key(destination));
        _operations.Add(sources.Count);
        for (var i = 0; i < sources.Count; i++) _operations.Add(Key(sources[i]));
        return destination;
    }

    internal static bool UsesOrderedPage(RedisQueryPlan plan) =>
        plan.Order is not null && !plan.CountOnly && plan.Take <= 256 && plan.Skip <= 1024;

    internal Task<RedisResult> Execute(IDatabase store, RedisKey matches, RedisQueryPlan plan, string orderPattern,
        string documentPattern, RedisKey orderIndex)
    {
        int matchIndex = Key(matches);
        bool orderedPage = UsesOrderedPage(plan);
        int orderKey = orderedPage ? Key(orderIndex) : 0;
        int selectedKey = orderedPage ? Key(Empty()) : 0;
        var arguments = new RedisValue[10 + _temporary.Count + _operations.Count];
        arguments[0] = matchIndex;
        arguments[1] = plan.CountOnly ? "1" : "0";
        arguments[2] = plan.Skip;
        arguments[3] = plan.Take;
        arguments[4] = plan.Descending ? "DESC" : "ASC";
        arguments[5] = orderPattern;
        arguments[6] = documentPattern;
        arguments[7] = _temporary.Count;
        for (var i = 0; i < _temporary.Count; i++) arguments[8 + i] = _temporary[i];
        arguments[8 + _temporary.Count] = orderKey;
        arguments[9 + _temporary.Count] = selectedKey;
        _operations.CopyTo(arguments, 10 + _temporary.Count);
        return store.ScriptEvaluateAsync(Script, _keys.ToArray(), arguments);
    }
}
