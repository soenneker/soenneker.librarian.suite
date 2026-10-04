using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Atomics.ValueBools;
using Soenneker.Dtos.IdValuePair;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Abstractions.Queries;
using Soenneker.Librarian.Abstractions.Serialization;
using Soenneker.Utils.PooledStringBuilders;
using StackExchange.Redis;

namespace Soenneker.Librarian.Redis;

public sealed partial class RedisLibrarianContainer : ILibrarianContainer
{
    private readonly RedisLibrarianDatabase _database;
    private readonly string _prefix;
    private readonly ConcurrentDictionary<Type, object> _queryRoots = new();
    private ValueAtomicBool _disposed = new(false);
    private readonly RedisKey Version;
    private readonly RedisKey Schema;
    private readonly RedisKey SortSchema;
    private readonly RedisKey Ids;
    private RedisKey Document(string id) => _prefix + "document:" + id;
    private string DocumentPattern(string field) => _prefix + "document:*->" + field;
    private static string Field(string path) => "index:" + RedisIndexValue.KeySegment(path);
    private static string SortField(string path) => "sort:" + RedisIndexValue.KeySegment(path);
    private RedisKey Index(string path) => IndexedKey("index:", path);
    private RedisKey Distinct(string path) => IndexedKey("distinct:", path);
    private RedisKey Present(string path) => IndexedKey("present:", path);
    private RedisKey Bucket(string path, string value) => IndexedKey("bucket:", path, value);

    internal RedisLibrarianContainer(string name, RedisLibrarianDatabase database)
    {
        _database = database;
        // One readable namespace hash tag lets a transaction include multiple containers in Redis Cluster.
        _prefix = database.StoragePrefix + RedisIndexValue.KeySegment(name) + ":";
        Version = _prefix + "version";
        Schema = _prefix + "schema";
        SortSchema = _prefix + "sort-schema";
        Ids = _prefix + "ids";
    }

    private string IndexedKey(string kind, string path, string? value = null)
    {
        var builder = new PooledStringBuilder(checked(_prefix.Length + kind.Length + path.Length * 4 + (value is null ? 0 : value.Length + 1)));
        try
        {
            builder.Append(_prefix);
            builder.Append(kind);
            builder.Append(RedisIndexValue.KeySegment(path));
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
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        return await _database.GetStore(token).NoSync();
    }

    private static string Id(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return RedisIndexValue.KeySegment(id.ToUpperInvariant());
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

    private const string MutationSnapshotScript = """
        return { redis.call('GET', KEYS[1]), redis.call('SMEMBERS', KEYS[2]), redis.call('HGET', KEYS[3], 'json') }
        """;

    private async ValueTask<bool> Mutate(string mode, string id, string? document, CancellationToken token)
    {
        string normalized = Id(id);
        if (mode != "delete") ArgumentNullException.ThrowIfNull(document);
        IDatabase store = await Store(token).NoSync();
        RedisKey[] keys = [Version, Schema, Document(normalized)];
        Abstractions.Transactions.LibrarianWrite[] writes = [new("unused", id, document)];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = (RedisResult[])(await store.ScriptEvaluateAsync(MutationSnapshotScript, keys).WaitAsync(token).NoSync())!;
            string? existing = snapshot[2].IsNull ? null : (string?)snapshot[2];
            if ((mode == "add" && existing is not null) || (mode == "update" && existing is null)) return false;
            if ((mode == "delete" && existing is null) || (mode != "delete" && string.Equals(existing, document, StringComparison.Ordinal))) return true;
            var commands = new RedisBatchCommands();
            commands.Version(Version, (RedisValue)snapshot[0], changed: true);
            await PrepareWrites(store, writes, (string[])snapshot[1]!, commands, token).NoSync();
            token.ThrowIfCancellationRequested();
            if ((long)await commands.Execute(store).NoSync() == 1) return true;
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
        RedisValue value = await store.HashGetAsync(Document(Id(id)), "json").WaitAsync(cancellationToken).NoSync();
        return value.IsNull ? null : value.ToString();
    }

    public async ValueTask<string> GetItemStrict(string id, CancellationToken cancellationToken = default) =>
        await GetItem(id, cancellationToken).NoSync() ?? throw new KeyNotFoundException($"Document '{id}' does not exist.");

    public async ValueTask<List<string>> GetAllItems(CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken).NoSync();
        RedisValue[] values = await store.SortAsync(Ids, sortType: SortType.Alphabetic, get: [DocumentPattern("json")]).WaitAsync(cancellationToken).NoSync();
        return values.Select(value => value.ToString()).ToList();
    }

    public async ValueTask<List<string>> GetAllIds(CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken).NoSync();
        RedisValue[] values = await store.SortAsync(Ids, sortType: SortType.Alphabetic, get: [DocumentPattern("id")]).WaitAsync(cancellationToken).NoSync();
        return values.Select(value => value.ToString()).ToList();
    }

    public async ValueTask<List<IdValuePair>> GetLibrarianItems(CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken).NoSync();
        RedisValue[] values = await store.SortAsync(Ids, sortType: SortType.Alphabetic, get: [DocumentPattern("id"), DocumentPattern("json")]).WaitAsync(cancellationToken).NoSync();
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
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await store.SetContainsAsync(Schema, fieldPath).WaitAsync(cancellationToken).NoSync()) return;
            RedisValue version = await store.StringGetAsync(Version).WaitAsync(cancellationToken).NoSync();
            RedisValue[] documents = await store.SortAsync(Ids, sortType: SortType.Alphabetic, get: ["#", DocumentPattern("json")]).WaitAsync(cancellationToken).NoSync();
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
                commands.Add(transaction.HashSetAsync(Document(entry.Id), SortField(fieldPath), entry.Value + "!" + entry.Id));
                commands.Add(transaction.SortedSetAddAsync(Index(fieldPath), entry.Value + "!" + entry.Id, 0));
                commands.Add(transaction.SortedSetAddAsync(Distinct(fieldPath), entry.Value, 0));
                commands.Add(transaction.SetAddAsync(Bucket(fieldPath, entry.Value), entry.Id));
                commands.Add(transaction.SetAddAsync(Present(fieldPath), entry.Id));
            }
            commands.Add(transaction.SetAddAsync(Schema, fieldPath));
            commands.Add(transaction.SetAddAsync(SortSchema, fieldPath));
            commands.Add(transaction.StringIncrementAsync(Version));
            if (await Commit(transaction, commands).NoSync()) return;
            await RetryIndexConflict(attempt, cancellationToken).NoSync();
        }
    }

    private static async ValueTask RetryIndexConflict(int attempt, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (attempt >= 7) throw new TimeoutException("The Librarian index repeatedly changed during the operation.");
        await Task.Delay(Random.Shared.Next(1, 10), token).NoSync();
    }

    private static object[] RangeArguments(RedisKey key, string min, string max, bool descending, int skip, int take) =>
        [key, descending ? max : min, descending ? min : max, "LIMIT", skip, take];

    private async ValueTask<LibrarianQueryResult<T>> Indexed<T>(string path, string min, string max, bool descending, int skip, int take, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        IDatabase store = await Store(token).NoSync();
        RedisIndexValue.ValidatePath(path);
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            RedisResult snapshot = await store.ScriptEvaluateAsync(IndexVersionScript, [Schema, Version], [path]).WaitAsync(token).NoSync();
            if (snapshot.IsNull) throw new InvalidOperationException($"Index '{path}' does not exist.");
            var version = (RedisValue)((RedisResult[])snapshot!)[0];
            RedisResult[] members = (RedisResult[]?)await store.ExecuteAsync(descending ? "ZREVRANGEBYLEX" : "ZRANGEBYLEX",
                RangeArguments(Index(path), min, max, descending, skip, take)).WaitAsync(token).NoSync() ?? [];
            var documents = new RedisValue[members.Length];
            var conflict = false;
            // Bound in-flight commands and observe cancellation between windows, including large backlogs.
            for (var start = 0; start < members.Length; start += 128)
            {
                token.ThrowIfCancellationRequested();
                int length = Math.Min(128, members.Length - start);
                var keys = new RedisKey[length + 1];
                keys[0] = Version;
                for (var i = 0; i < length; i++)
                {
                    var member = members[start + i].ToString();
                    keys[i + 1] = Document(member[(member.IndexOf('!') + 1)..]);
                }
                RedisResult read = await store.ScriptEvaluateAsync(ReadIndexedItemsScript, keys,
                    [version.IsNull ? "0" : "1", version.IsNull ? "" : version]).WaitAsync(token).NoSync();
                if (read.IsNull) { conflict = true; break; }
                var values = (RedisResult[])read!;
                for (var i = 0; i < values.Length; i++) documents[start + i] = (RedisValue)values[i];
            }
            if (conflict)
            {
                await RetryIndexConflict(attempt, token).NoSync();
                continue;
            }
            var items = new List<T>(documents.Length);
            foreach (RedisValue document in documents)
            {
                token.ThrowIfCancellationRequested();
                items.Add(LibrarianJson.Deserialize<T>(document.ToString())!);
            }
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
        string encoded = RedisIndexValue.Encode(value);
        return await CountIndex(fieldPath, "[" + encoded + "!", "[" + encoded + "!~", cancellationToken).NoSync();
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
        _ = LibrarianJson.Contract(typeof(T));
        ObjectDisposedException.ThrowIf(_disposed.Value, this);
        return (IQueryable<T>)_queryRoots.GetOrAdd(typeof(T), static (_, container) =>
            new LibrarianQueryable<T>(new RedisQueryProvider<T>(container)), this);
    }

    internal async ValueTask<RedisResult> ExecuteQuery(RedisQueryPlan plan, CancellationToken cancellationToken = default)
    {
        IDatabase store = await Store(cancellationToken).NoSync();
        await EnsureQueryIndexes(store, plan, cancellationToken).NoSync();
        // SCARD and SORT each execute atomically in Redis. A persistent set needs no optimistic retry or temporary set.
        if (plan.Order is null && TryDirectSet(plan.Filter, out RedisKey direct))
            return await ReadQuerySet(store, direct, plan, cancellationToken).NoSync();
        var commands = new RedisQueryCommands(_prefix);
        RedisKey matches = Evaluate(plan.Filter, commands);
        if (plan.Order is not null)
            matches = commands.Combine("SINTERSTORE", [matches, Present(plan.Order)]);
        return await commands.Execute(store, matches, plan,
            plan.Order is null ? "" : DocumentPattern(SortField(plan.Order)), DocumentPattern("json"),
            plan.Order is null ? default : Index(plan.Order))
            .WaitAsync(cancellationToken).NoSync();
    }

    private async ValueTask<RedisResult> ReadQuerySet(IDatabase store, RedisKey matches, RedisQueryPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.CountOnly)
        {
            long count = await store.SetLengthAsync(matches).NoSync();
            return RedisResult.Create(Math.Min(plan.Take, Math.Max(0, count - plan.Skip)));
        }
        RedisValue[] documents = plan.Take == 0 ? [] : await store.SortAsync(matches, skip: plan.Skip, take: plan.Take,
            order: plan.Descending ? Order.Descending : Order.Ascending, sortType: SortType.Alphabetic,
            by: plan.Order is null ? default : DocumentPattern(SortField(plan.Order)), get: [DocumentPattern("json")]).NoSync();
        return RedisResult.Create(documents);
    }

    private bool TryDirectSet(RedisQueryFilter filter, out RedisKey key)
    {
        if (filter.Operation == "all") { key = Ids; return true; }
        if (filter.Operation == "term" && filter.Minimum[0] == '[' && filter.Maximum.Length == filter.Minimum.Length + 1
            && filter.Maximum[^1] == '~' && filter.Maximum.AsSpan(0, filter.Minimum.Length).SequenceEqual(filter.Minimum))
        {
            key = Bucket(filter.Path!, filter.Minimum[1..^1]);
            return true;
        }
        key = default;
        return false;
    }

    private RedisKey Evaluate(RedisQueryFilter filter, RedisQueryCommands commands)
    {
        if (filter.Operation == "none") return commands.Empty();
        if (filter.Operation == "in") return commands.Combine("SUNIONSTORE",
            filter.Values!.Select(value => Bucket(filter.Path!, value)).ToArray());
        if (TryDirectSet(filter, out RedisKey direct)) return direct;
        if (filter.Operation == "term") return commands.Range(Index(filter.Path!), filter.Minimum, filter.Maximum);
        RedisKey left = Evaluate(filter.Left!, commands);
        if (filter.Operation == "not") return commands.Combine("SDIFFSTORE", [Ids, left]);
        RedisKey right = Evaluate(filter.Right!, commands);
        return commands.Combine(filter.Operation == "and" ? "SINTERSTORE" : "SUNIONSTORE", [left, right]);
    }

    public void Dispose()
    {
        if (!_disposed.TrySetTrue()) return;
        _queryRoots.Clear();
    }
}
