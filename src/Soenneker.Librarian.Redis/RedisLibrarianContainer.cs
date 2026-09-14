using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Hashing.Sha256.Abstract;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Utils.Json;
using Soenneker.Utils.PooledStringBuilders;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianContainer : ILibrarianContainer
{
    private readonly RedisLibrarianDatabase _database;
    private readonly string _prefix;
    private volatile bool _disposed;
    private RedisKey Version => _prefix + "version";
    private RedisKey Schema => _prefix + "schema";
    private RedisKey Ids => _prefix + "ids";
    private RedisKey Document(string id) => _prefix + "document:" + id;
    private string DocumentPattern(string field) => _prefix + "document:*->" + field;
    private static string Field(string path) => RedisIndexValue.Hex(path, "index:");
    private RedisKey Index(string path) => IndexedKey("index:", path);
    private RedisKey Distinct(string path) => IndexedKey("distinct:", path);
    private RedisKey Present(string path) => IndexedKey("present:", path);
    private RedisKey Bucket(string path, string value) => IndexedKey("bucket:", path, value);

    internal RedisLibrarianContainer(string key, string name, RedisLibrarianDatabase database, ISha256HashingUtil sha256HashingUtil)
    {
        _database = database;
        var identity = new PooledStringBuilder(checked(key.Length * 4));
        try
        {
            RedisIndexValue.AppendHex(ref identity, key);
            // One database hash tag lets a transaction include multiple containers in Redis Cluster.
            _prefix = "librarian:{" + sha256HashingUtil.Hash(identity.AsSpan()).ToUpperInvariant() + "}:batches:" + RedisIndexValue.Hex(name) + ":";
        }
        finally { identity.Dispose(); }
    }

    private string IndexedKey(string kind, string path, string? value = null)
    {
        var builder = new PooledStringBuilder(checked(_prefix.Length + kind.Length + path.Length * 4 + (value is null ? 0 : value.Length + 1)));
        try
        {
            builder.Append(_prefix);
            builder.Append(kind);
            RedisIndexValue.AppendHex(ref builder, path);
            if (value is not null)
            {
                builder.Append(':');
                builder.Append(value);
            }
            return builder.ToString();
        }
        finally { builder.Dispose(); }
    }

    private async ValueTask<IDatabase> Store(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return await _database.GetStore(token).NoSync();
    }

    private static string Id(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return RedisIndexValue.Hex(id.ToUpperInvariant());
    }

    private ITransaction Transaction(IDatabase store, RedisValue version)
    {
        ITransaction transaction = store.CreateTransaction();
        transaction.AddCondition(version.IsNull ? Condition.KeyNotExists(Version) : Condition.StringEqual(Version, version));
        return transaction;
    }

    private static async ValueTask<bool> Commit(ITransaction transaction, List<Task> commands)
    {
        bool committed = await transaction.ExecuteAsync().NoSync();
        try { await Task.WhenAll(commands).NoSync(); }
        catch (TaskCanceledException) when (!committed) { }
        return committed;
    }

    private async ValueTask<bool> Mutate(string mode, string id, string? document, CancellationToken token)
    {
        string normalized = Id(id);
        if (mode != "delete") ArgumentNullException.ThrowIfNull(document);
        IDatabase store = await Store(token).NoSync();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            RedisValue version = await store.StringGetAsync(Version).NoSync();
            RedisValue[] paths = await store.SetMembersAsync(Schema).NoSync();
            RedisValue existing = await store.HashGetAsync(Document(normalized), "json").NoSync();
            if ((mode == "add" && !existing.IsNull) || (mode == "update" && existing.IsNull)) return false;
            if (mode == "delete" && existing.IsNull) return true;
            using JsonDocument? json = mode != "delete" && paths.Length > 0 ? JsonDocument.Parse(document!) : null;
            // Read and validate everything before queueing a transaction. A version condition protects all reads.
            var values = new List<(string Path, RedisValue Old, string? New, long OldCount)>();
            foreach (RedisValue pathValue in paths)
            {
                var path = pathValue.ToString();
                RedisValue old = await store.HashGetAsync(Document(normalized), Field(path)).NoSync();
                string? value = json is null ? null : RedisIndexValue.Read(json.RootElement, path);
                long count = old.IsNull ? 0 : await store.SetLengthAsync(Bucket(path, old.ToString())).NoSync();
                values.Add((path, old, value, count));
            }
            token.ThrowIfCancellationRequested();
            ITransaction transaction = Transaction(store, version);
            var commands = new List<Task>();
            foreach ((string Path, RedisValue Old, string? New, long OldCount) value in values)
            {
                if (!value.Old.IsNull)
                {
                    commands.Add(transaction.SortedSetRemoveAsync(Index(value.Path), value.Old + "!" + normalized));
                    commands.Add(transaction.SetRemoveAsync(Bucket(value.Path, value.Old.ToString()), normalized));
                    if (value.OldCount == 1) commands.Add(transaction.SortedSetRemoveAsync(Distinct(value.Path), value.Old));
                }
                if (value.New is null)
                {
                    commands.Add(transaction.HashDeleteAsync(Document(normalized), Field(value.Path)));
                    commands.Add(transaction.SetRemoveAsync(Present(value.Path), normalized));
                }
                else
                {
                    commands.Add(transaction.HashSetAsync(Document(normalized), Field(value.Path), value.New));
                    commands.Add(transaction.SortedSetAddAsync(Index(value.Path), value.New + "!" + normalized, 0));
                    commands.Add(transaction.SortedSetAddAsync(Distinct(value.Path), value.New, 0));
                    commands.Add(transaction.SetAddAsync(Bucket(value.Path, value.New), normalized));
                    commands.Add(transaction.SetAddAsync(Present(value.Path), normalized));
                }
            }
            if (mode == "delete")
            {
                commands.Add(transaction.KeyDeleteAsync(Document(normalized)));
                commands.Add(transaction.SetRemoveAsync(Ids, normalized));
            }
            else
            {
                commands.Add(transaction.HashSetAsync(Document(normalized), "json", document!));
                commands.Add(transaction.HashSetAsync(Document(normalized), "id", id, When.NotExists));
                commands.Add(transaction.SetAddAsync(Ids, normalized));
            }
            commands.Add(transaction.StringIncrementAsync(Version));
            if (await Commit(transaction, commands).NoSync()) return true;
        }
    }

    public async ValueTask<string> AddItem(string id, string document, CancellationToken cancellationToken = default)
    {
        if (!await Mutate("add", id, document, cancellationToken).NoSync()) throw new InvalidOperationException($"Document '{id}' already exists.");
        return document;
    }

    public async ValueTask<string?> UpdateItem(string id, string document, CancellationToken cancellationToken = default) =>
        await Mutate("update", id, document, cancellationToken).NoSync() ? document : null;

    public async ValueTask<string> UpdateItemStrict(string id, string document, CancellationToken cancellationToken = default) =>
        await UpdateItem(id, document, cancellationToken).NoSync() ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask DeleteItem(string id, CancellationToken cancellationToken = default) =>
        _ = await Mutate("delete", id, null, cancellationToken).NoSync();

    public async ValueTask<string?> GetItem(string id, CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken).NoSync();
        RedisValue value = await store.HashGetAsync(Document(Id(id)), "json").NoSync();
        return value.IsNull ? null : value.ToString();
    }

    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).NoSync() ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken).NoSync();
        RedisValue[] values = await store.SortAsync(Ids, sortType: SortType.Alphabetic, get: [DocumentPattern("json")]).NoSync();
        return values.Select(value => value.ToString()).ToList();
    }

    public async ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken).NoSync();
        RedisValue[] values = await store.SortAsync(Ids, sortType: SortType.Alphabetic, get: [DocumentPattern("id")]).NoSync();
        return values.Select(value => value.ToString()).ToList();
    }

    public async ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken).NoSync();
        RedisValue[] values = await store.SortAsync(Ids, sortType: SortType.Alphabetic, get: [DocumentPattern("id"), DocumentPattern("json")]).NoSync();
        var items = new List<IdValuePair>(values.Length / 2);
        for (var i = 0; i < values.Length; i += 2) items.Add(new IdValuePair { Id = values[i].ToString(), Value = values[i + 1].ToString() });
        return items;
    }

    public async ValueTask DeleteAllItems(CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken).NoSync();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RedisValue version = await store.StringGetAsync(Version).NoSync();
            RedisValue[] paths = await store.SetMembersAsync(Schema).NoSync();
            var keys = new List<RedisKey> { Ids };
            foreach (RedisValue id in await store.SetMembersAsync(Ids).NoSync()) keys.Add(Document(id.ToString()));
            foreach (RedisValue path in paths)
            {
                var name = path.ToString();
                keys.Add(Index(name)); keys.Add(Distinct(name)); keys.Add(Present(name));
                foreach (RedisValue value in await store.SortedSetRangeByRankAsync(Distinct(name)).NoSync()) keys.Add(Bucket(name, value.ToString()));
            }
            cancellationToken.ThrowIfCancellationRequested();
            ITransaction transaction = Transaction(store, version);
            var commands = new List<Task> { transaction.KeyDeleteAsync(keys.ToArray()), transaction.StringIncrementAsync(Version) };
            if (await Commit(transaction, commands).NoSync()) return;
        }
    }

    public async ValueTask EnsureIndex(string fieldPath, CancellationToken cancellationToken = default)
    {
        RedisIndexValue.ValidatePath(fieldPath);
        IDatabase store = await Store(cancellationToken).NoSync();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RedisValue version = await store.StringGetAsync(Version).NoSync();
            if (await store.SetContainsAsync(Schema, fieldPath).NoSync()) return;
            RedisValue[] documents = await store.SortAsync(Ids, sortType: SortType.Alphabetic, get: ["#", DocumentPattern("json")]).NoSync();
            var entries = new List<(string Id, string Value)>();
            for (var i = 0; i < documents.Length; i += 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using JsonDocument json = JsonDocument.Parse(documents[i + 1].ToString());
                string? value = RedisIndexValue.Read(json.RootElement, fieldPath);
                if (value is not null) entries.Add((documents[i].ToString(), value));
            }
            cancellationToken.ThrowIfCancellationRequested();
            ITransaction transaction = Transaction(store, version);
            var commands = new List<Task>();
            foreach ((string Id, string Value) entry in entries)
            {
                commands.Add(transaction.HashSetAsync(Document(entry.Id), Field(fieldPath), entry.Value));
                commands.Add(transaction.SortedSetAddAsync(Index(fieldPath), entry.Value + "!" + entry.Id, 0));
                commands.Add(transaction.SortedSetAddAsync(Distinct(fieldPath), entry.Value, 0));
                commands.Add(transaction.SetAddAsync(Bucket(fieldPath, entry.Value), entry.Id));
                commands.Add(transaction.SetAddAsync(Present(fieldPath), entry.Id));
            }
            commands.Add(transaction.SetAddAsync(Schema, fieldPath));
            commands.Add(transaction.StringIncrementAsync(Version));
            if (await Commit(transaction, commands).NoSync()) return;
        }
    }

    private async ValueTask RequireIndex(IDatabase store, string path)
    {
        RedisIndexValue.ValidatePath(path);
        if (!await store.SetContainsAsync(Schema, path).NoSync()) throw new InvalidOperationException($"Index '{path}' does not exist.");
    }

    private static object[] RangeArguments(RedisKey key, string min, string max, bool descending, int skip, int take) =>
        [key, descending ? max : min, descending ? min : max, "LIMIT", skip, take];

    private async ValueTask<LibrarianQueryResult<T>> Indexed<T>(string path, string min, string max, bool descending, int skip, int take, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        IDatabase store = await Store(token).NoSync();
        await RequireIndex(store, path).NoSync();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            RedisValue version = await store.StringGetAsync(Version).NoSync();
            RedisResult[] members = (RedisResult[]?)await store.ExecuteAsync(descending ? "ZREVRANGEBYLEX" : "ZRANGEBYLEX",
                RangeArguments(Index(path), min, max, descending, skip, take)).NoSync() ?? [];
            var reads = new Task<RedisValue>[members.Length];
            for (var i = 0; i < members.Length; i++)
            {
                var member = members[i].ToString();
                reads[i] = store.HashGetAsync(Document(member[(member.IndexOf('!') + 1)..]), "json");
            }
            RedisValue[] documents = await Task.WhenAll(reads).NoSync();
            if (version != await store.StringGetAsync(Version).NoSync()) continue;
            List<T> items = documents.Select(document => JsonUtil.Deserialize<T>(document.ToString())!).ToList();
            return new LibrarianQueryResult<T> { Items = items, Index = path, IndexEntriesExamined = members.Length, DocumentsDeserialized = documents.Length };
        }
    }

    public ValueTask<LibrarianQueryResult<T>> FindByIndex<T>(string fieldPath, object? value, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        string encoded = RedisIndexValue.Encode(value);
        return Indexed<T>(fieldPath, "[" + encoded + "!", "[" + encoded + "!~", false, skip, take, cancellationToken);
    }

    public async ValueTask<int> CountByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken).NoSync();
        await RequireIndex(store, fieldPath).NoSync();
        string encoded = RedisIndexValue.Encode(value);
        return checked((int)(long)await store.ExecuteAsync("ZLEXCOUNT", Index(fieldPath), "[" + encoded + "!", "[" + encoded + "!~").NoSync());
    }

    public async ValueTask<bool> ExistsByIndex(string fieldPath, object? value, CancellationToken cancellationToken = default) =>
        await CountByIndex(fieldPath, value, cancellationToken).NoSync() != 0;

    public ValueTask<LibrarianQueryResult<T>> FindRangeByIndex<T>(string fieldPath, object? minimum = null, object? maximum = null,
        bool descending = false, int skip = 0, int take = 100, CancellationToken cancellationToken = default)
    {
        string? min = minimum is null ? null : RedisIndexValue.Encode(minimum);
        string? max = maximum is null ? null : RedisIndexValue.Encode(maximum);
        if (min is not null && max is not null && min[0] != max[0]) throw new ArgumentException("Range bounds must have the same scalar type.");
        return Indexed<T>(fieldPath, min is null ? "-" : "[" + min + "!", max is null ? "+" : "[" + max + "!~", descending, skip, take, cancellationToken);
    }

    public IQueryable<T> BuildQueryable<T>()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new RedisQueryable<T>(new RedisQueryProvider<T>(this));
    }

    internal async ValueTask<RedisResult> ExecuteQuery(RedisQueryPlan plan)
    {
        foreach (string path in plan.Paths) await EnsureIndex(path).NoSync();
        IDatabase store = await Store(CancellationToken.None).NoSync();
        while (true)
        {
            RedisValue version = await store.StringGetAsync(Version).NoSync();
            var temporaryKeys = new List<RedisKey>();
            long started = Stopwatch.GetTimestamp();
            try
            {
                RedisKey matches = await Evaluate(store, plan.Filter, temporaryKeys).NoSync();
                if (plan.Order is not null) matches = await Combine(store, SetOperation.Intersect, [matches, Present(plan.Order)], temporaryKeys).NoSync();
                RedisResult result;
                if (plan.CountOnly)
                {
                    long count = await store.SetLengthAsync(matches).NoSync();
                    result = RedisResult.Create(Math.Min(plan.Take, Math.Max(0, count - plan.Skip)));
                }
                else
                {
                    RedisValue[] documents = plan.Take == 0 ? [] : await store.SortAsync(matches, skip: plan.Skip, take: plan.Take,
                        order: plan.Descending ? Order.Descending : Order.Ascending, sortType: SortType.Alphabetic,
                        by: plan.Order is null ? default : DocumentPattern(Field(plan.Order)), get: [DocumentPattern("json")]).NoSync();
                    result = RedisResult.Create(documents);
                }
                // Never accept a result if temporary sets could have expired during a long attempt.
                if (version == await store.StringGetAsync(Version).NoSync() && Stopwatch.GetElapsedTime(started) < TimeSpan.FromMinutes(4)) return result;
            }
            finally
            {
                if (temporaryKeys.Count > 0) await store.KeyDeleteAsync(temporaryKeys.ToArray()).NoSync();
            }
        }
    }

    private async ValueTask<RedisKey> Evaluate(IDatabase store, RedisQueryFilter filter, List<RedisKey> temporaryKeys)
    {
        if (filter.Operation == "all") return Ids;
        if (filter.Operation == "term")
        {
            // Only distinct encoded index values cross the network; Redis combines their document-ID sets.
            string Bound(string bound) => bound is "-" or "+" ? bound : bound[..bound.IndexOf('!')];
            RedisResult[] values = (RedisResult[]?)await store.ExecuteAsync("ZRANGEBYLEX", Distinct(filter.Path!), Bound(filter.Minimum), Bound(filter.Maximum)).NoSync() ?? [];
            return await Combine(store, SetOperation.Union, values.Select(value => Bucket(filter.Path!, value.ToString())).ToArray(), temporaryKeys).NoSync();
        }
        RedisKey left = await Evaluate(store, filter.Left!, temporaryKeys).NoSync();
        if (filter.Operation == "not") return await Combine(store, SetOperation.Difference, [Ids, left], temporaryKeys).NoSync();
        RedisKey right = await Evaluate(store, filter.Right!, temporaryKeys).NoSync();
        return await Combine(store, filter.Operation == "and" ? SetOperation.Intersect : SetOperation.Union, [left, right], temporaryKeys).NoSync();
    }

    private async ValueTask<RedisKey> Combine(IDatabase store, SetOperation operation, RedisKey[] keys, List<RedisKey> temporaryKeys)
    {
        RedisKey destination = _prefix + "query:" + Guid.NewGuid().ToString("N");
        temporaryKeys.Add(destination);
        if (keys.Length == 0) return destination;
        ITransaction transaction = store.CreateTransaction();
        var commands = new List<Task>
        {
            transaction.SetCombineAndStoreAsync(operation, destination, keys),
            transaction.KeyExpireAsync(destination, TimeSpan.FromMinutes(5))
        };
        await Commit(transaction, commands).NoSync();
        return destination;
    }

    public void Dispose() => _disposed = true;
}
