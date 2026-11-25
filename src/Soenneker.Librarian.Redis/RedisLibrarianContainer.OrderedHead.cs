using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.Task;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianContainer
{
    private const int HeadSize = 32;
    private const int MaxPredicateArguments = 128;
    private const string HeadScript = """
        local items = redis.call(ARGV[1], KEYS[2], 0, ARGV[2])
        return {redis.call('GET', KEYS[1]), items}
        """;

    // Evaluate the predicate on a bounded prefix of the order index. Declare every document
    // key and check the version before returning JSON. Sparse queries use the existing set plan.
    private const string FilterHeadScript = """
        if (redis.call('GET', KEYS[1]) or '') ~= ARGV[1] then return false end
        local skip, take = tonumber(ARGV[2]), tonumber(ARGV[3])
        local rows = {}
        for i = 2, #KEYS do
            local stack, p = {}, 4 + #KEYS
            while p <= #ARGV do
                local op = ARGV[p]
                p = p + 1
                if op == 'and' or op == 'or' then
                    local right = table.remove(stack)
                    local left = table.remove(stack)
                    if op == 'and' then stack[#stack+1] = left and right
                    else stack[#stack+1] = left or right end
                elseif op == 'not' then stack[#stack] = not stack[#stack]
                elseif op == 'all' or op == 'none' then stack[#stack+1] = op == 'all'
                else
                    local value = redis.call('HGET', KEYS[i], ARGV[p])
                    p = p + 1
                    local match = false
                    if op == 'term' then
                        local lo, hi = ARGV[p], ARGV[p+1]
                        p = p + 2
                        if value then
                            -- IDs sort between the index's inclusive/exclusive sentinels.
                            local member = value .. '!' .. ARGV[4+i-1]
                            match = (lo == '-' or member > string.sub(lo,2) or
                                (string.sub(lo,1,1) == '[' and member == string.sub(lo,2))) and
                                (hi == '+' or member < string.sub(hi,2) or
                                (string.sub(hi,1,1) == '[' and member == string.sub(hi,2)))
                        end
                    else
                        local n = tonumber(ARGV[p])
                        p = p + 1
                        for j = p, p+n-1 do if value == ARGV[j] then match = true end end
                        p = p + n
                    end
                    stack[#stack+1] = match
                end
            end
            if stack[1] then
                if skip > 0 then skip = skip - 1
                else
                    rows[#rows+1] = redis.call('HGET', KEYS[i], 'json')
                    if #rows == take then return rows end
                end
            end
        end
        if ARGV[4] == '1' then return rows end
        return false
        """;

    private async Task<RedisResult?> TryOrderedHead(IDatabase store, RedisQueryPlan plan, CancellationToken token)
    {
        if (plan.Take == 0) return RedisResult.Create(Array.Empty<RedisValue>());
        // Keep Lua work bounded even when an IN predicate contains a large client collection.
        var predicate = new List<RedisValue>();
        if (!AppendFilter(plan.Filter, predicate)) return null;
        var head = (RedisResult[])(await store.ScriptEvaluateAsync(HeadScript, [Version, Index(plan.Order!)],
            [plan.Descending ? "ZREVRANGE" : "ZRANGE", HeadSize - 1]).WaitAsync(token).NoSync())!;
        var members = (string[])head[1]!;
        var keys = new RedisKey[members.Length + 1];
        keys[0] = Version;
        List<RedisValue> args = [head[0].IsNull ? "" : head[0].ToString(), plan.Skip, plan.Take, members.Length < HeadSize ? "1" : "0"];
        for (int i = 0; i < members.Length; i++)
        {
            string id = members[i][(members[i].IndexOf('!') + 1)..];
            keys[i + 1] = Document(id);
            args.Add(id);
        }
        args.AddRange(predicate);
        RedisResult result = await store.ScriptEvaluateAsync(FilterHeadScript, keys, args.ToArray()).WaitAsync(token).NoSync();
        return result.IsNull ? null : result;
    }

    private static bool AppendFilter(RedisQueryFilter filter, List<RedisValue> args)
    {
        if (args.Count >= MaxPredicateArguments ||
            filter.Left is not null && !AppendFilter(filter.Left, args) ||
            filter.Right is not null && !AppendFilter(filter.Right, args)) return false;
        args.Add(filter.Operation);
        if (filter.Operation == "term")
        {
            args.Add(Field(filter.Path!));
            args.Add(filter.Minimum);
            args.Add(filter.Maximum);
        }
        else if (filter.Operation == "in")
        {
            args.Add(Field(filter.Path!));
            if (filter.Values!.Length > MaxPredicateArguments - args.Count - 1) return false;
            args.Add(filter.Values.Length);
            foreach (string value in filter.Values) args.Add(value);
        }
        return args.Count <= MaxPredicateArguments;
    }
}
