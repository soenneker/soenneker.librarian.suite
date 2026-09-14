# Redis provider

[Back to the README](../README.md#redis)

`Soenneker.Librarian.Redis` stores documents and indexes in Redis through `Soenneker.Redis.Client`. See the README for package installation and registration.

## Configuration

| Setting | Required | Meaning |
| --- | --- | --- |
| `Azure:Redis:ConnectionString` | Yes | Connection used by `Soenneker.Redis.Client` |
| `Librarian:Redis:Key` | Yes | Database namespace owned by this provider |
| `Librarian:Redis:KeyPrefix` | No | Top-level key prefix; defaults to `librarian` |
| `Librarian:Redis:Database` | No | Redis database number; defaults to `-1`, the connection default |

Both `AddRedisLibrarianDatabaseAsSingleton()` and `AddRedisLibrarianDatabaseAsScoped()` are available. The provider does not dispose the injected client or its shared multiplexer.

## Storage and consistency

| Operation | Behavior |
| --- | --- |
| Add, update, delete | Commits to Redis before returning |
| Read | Retrieves current server data, including writes from other instances |
| `Save()` / `MarkDirty()` | Compatibility no-ops |
| Unload | Releases the local container handle; stored data remains |
| Disposal | No document flush is needed |
| `DeleteAllItems()` | Removes documents but preserves index definitions |

- Each document has its own Redis hash. IDs are case-insensitive; container names are case-sensitive.
- Persistent sorted sets and equality sets hold indexes.
- Conditional transactions (`WATCH` / `MULTI` / `EXEC`) update documents and indexes together. Version checks retry concurrent changes.
- Reads repeat when version checks detect overlapping writes.
- There is no document cache, dirty tracking, or periodic save.
- Cancellation is checked before dispatch. Already dispatched commands are awaited and may commit.

## Redis key layout

Container names, document IDs, and index paths are readable in Redis browsers:

```text
flywheel:{my-app}:containers:flywheel.jobs:document:JOB-123
flywheel:{my-app}:containers:flywheel.jobs:ids
flywheel:{my-app}:containers:flywheel.jobs:index:value.state
```

Set `Librarian:Redis:KeyPrefix` to `flywheel`, or pass `keyPrefix: "flywheel"` to either explicit database constructor. `Key` selects the readable namespace (`my-app` above); its braces form a Redis Cluster hash tag that keeps the containers together. The namespace is not hashed. Use the same prefix and namespace on every instance that shares data.

ASCII letters, digits, dots, hyphens, and underscores remain readable. Other UTF-16 characters are escaped as `%XXXX` to protect key separators and Redis patterns. Document IDs are normalized to uppercase for case-insensitive lookup; the hash's `id` field retains the original spelling. Index values retain their sortable encoding to preserve exact range ordering.

This `:containers:` layout replaces the hex-encoded `:batches:` layout. Existing data is not read or migrated automatically.

## Supported queries

| Supported | Details |
| --- | --- |
| Scalar comparisons | Indexed JSON properties |
| Boolean expressions | AND, OR, NOT |
| Ordering | One `OrderBy` or `OrderByDescending` |
| Paging | `Skip` and `Take` after filtering and ordering |
| Aggregates | `Count`, `LongCount`, `Any` |
| Single results | `First`, `Single`, and their default variants |

Unsupported expressions throw `NotSupportedException`. Queries do not fall back to loading all documents for local filtering.

LINQ enumeration executes synchronously. Use `FindByIndex`, `FindRangeByIndex`, `CountByIndex`, and `ExistsByIndex` for asynchronous execution.

For projection, materialize the server page first:

```csharp
var page = users.BuildQueryable<User>()
    .Where(user => user.Age >= 18)
    .OrderBy(user => user.Age)
    .Take(25)
    .ToList();

var names = page.Select(user => user.Name).ToList();
```

This example uses the `users` container and `User` model from the [README](../README.md#query-documents).

## Index behavior and cost

- JSON paths use camel case or `[JsonPropertyName]`; missing indexed fields are excluded from matches.
- `EnsureIndex()` reads existing documents to encode exact decimal and ordinal string values, then installs the index only if its snapshot is still current.
- Index creation is proportional to container size. Definitions survive unload, restart, and document deletion.
- Simple index pages seek within sorted sets and transfer only the requested documents.
- Compound LINQ plans retrieve distinct encoded index values, then use Redis union, intersection, difference, and `SORT` operations to filter, order, and page on the server.
- Broad filters consume Redis CPU and temporary memory. Compound queries may transfer many distinct index values even when the returned document page is small.
- Temporary result sets are deleted after a query and expire for crash cleanup.

## Deployment

- No Lua scripts, Redis Search, or RedisJSON are required.
- All containers in a database namespace share a hash slot, allowing conditional batches across containers. Cluster deployments require Redis 8 or later for `SORT` with external key patterns.
- Configure Redis persistence for the durability you need and use a non-evicting database for document storage.
- Keep the configured namespace under the provider's ownership; external key changes bypass document/index coordination.
- The current `:containers:` layout does not read earlier `:batches:`, `:native:`, snapshot, or scripted prototype layouts.

## Integration tests

Set `LIBRARIAN_TEST_REDIS` to a test Redis connection string, then run from the repository root:

```sh
dotnet test --project test/Soenneker.Librarian.Suite.Tests -- --treenode-filter "/*/*/RedisPersistenceTests/*"
```

Tests use unique namespaces and remove their keys afterward. Without the environment variable, Redis integration tests are skipped.
