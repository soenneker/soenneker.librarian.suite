[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.librarian.suite/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.librarian.suite/actions/workflows/publish-package.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.librarian.suite/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.librarian.suite/actions/workflows/codeql.yml)

# Soenneker.Librarian

Document storage for .NET 10 with interchangeable memory, JSON file, Redis, and PostgreSQL providers.

- **Async document operations** — store, retrieve, update, and delete JSON by ID.
- **Atomic batches** — update related documents together, with conditions to prevent conflicting writes.
- **Typed repositories** — work with document classes through `ILibrarianRepository<TDocument>`.
- **Indexed queries** — filter, sort, count, and page with LINQ or explicit async index methods.

[Quick start](#quick-start) · [Providers](#choose-a-provider) · [Queries](#query-documents) · [Typed repositories](#typed-repositories) · [Performance](#performance)

## Installation

Install the provider you need. Shared dependencies are included automatically.

```sh
dotnet add package Soenneker.Librarian.Memory
# Or: dotnet add package Soenneker.Librarian.FileSystem
# Or: dotnet add package Soenneker.Librarian.Redis
# Or: dotnet add package Soenneker.Librarian.Postgres
```

| Package | Purpose |
| --- | --- |
| `Soenneker.Librarian.Memory` | In-process document storage |
| `Soenneker.Librarian.FileSystem` | In-memory documents backed by a JSON file |
| `Soenneker.Librarian.Redis` | Shared document storage and indexes in Redis |
| `Soenneker.Librarian.Postgres` | PostgreSQL persistence, SQL queries, and atomic transactions |
| `Soenneker.Librarian.Core` | Typed repository and local container implementation |
| `Soenneker.Librarian.Abstractions` | Database, container, and repository contracts |

## Quick start

This standalone example registers the memory provider, stores a document, and reads it back:

```csharp
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Memory.Registrars;

var services = new ServiceCollection();
services.AddLogging();
services.AddMemoryLibrarianDatabaseAsSingleton();

await using var provider = services.BuildServiceProvider();
var database = provider.GetRequiredService<ILibrarianDatabase>();
var users = await database.GetContainer("users");

await users.AddItem("user-1", """{"name":"Alex","age":30,"active":true}""");
string? json = await users.GetItem("user-1");
```

In a hosted application, register the provider on `builder.Services` and inject `ILibrarianDatabase` into your service.

## Choose a provider

| | Memory | FileSystem | Redis | PostgreSQL |
| --- | --- | --- | --- | --- |
| Use when | Data can be temporary | One process needs local persistence | Multiple instances share documents | Shared persistent documents with SQL queries |
| Documents live in | Process memory | Process memory, backed by JSON | Redis | PostgreSQL |
| Writes persist | Never | Periodic save, about every 5 seconds; atomic batches save immediately | Before the mutation returns | Before the mutation returns |
| Explicit flush | No-op | `await database.Save()` | No-op | No-op |
| Index lifetime | Until unload or disposal | Rebuilt after unload or restart | Persisted in Redis | Persisted in PostgreSQL |

Register one provider for `ILibrarianDatabase`. Each registrar also offers an `AsScoped()` variant; a scoped memory database has its own data.

### PostgreSQL

Use `Soenneker.Librarian.Postgres` for shared persistent documents with SQL-backed filtering, sorting, paging, and aggregates. Writes and cross-container conditional batches commit immediately; indexes persist across restarts. It requires PostgreSQL 16 or later.

```csharp
using Soenneker.Librarian.Postgres.Registrars;

builder.Services.AddPostgresLibrarianDatabaseAsSingleton();
```

Configure `Librarian:Postgres:ConnectionString` and `Librarian:Postgres:Key`. See [PostgreSQL setup, queries, and transaction semantics](docs/POSTGRES.md).

### FileSystem

```csharp
using Soenneker.Librarian.FileSystem.Registrars;

builder.Services.AddFileSystemLibrarianDatabaseAsSingleton();
```

Add to `appsettings.json`:

```json
{
  "Librarian": {
    "FileSystem": { "FilePath": "data/librarian.json" }
  }
}
```

- Use one database owner per file.
- Await `database.Save()` when changes must be flushed explicitly.
- Dispose the database's owner asynchronously to complete shutdown persistence.

### Redis

```csharp
using Soenneker.Librarian.Redis.Registrars;

builder.Services.AddRedisLibrarianDatabaseAsSingleton();
```

Add to application configuration:

```json
{
  "Azure": { "Redis": { "ConnectionString": "localhost:6379" } },
  "Librarian": { "Redis": { "Key": "my-app:librarian", "KeyPrefix": "librarian", "Database": -1 } }
}
```

- `Key` is the required namespace; `Database` is optional (`-1` uses the connection default).
- Reads fetch current Redis data; writes update documents and indexes together.
- No RedisJSON, Redis Search, or Lua scripts required.
- Redis persistence and eviction settings determine durability. Use a non-evicting database for document storage.
- Cluster deployments require Redis 8 or later.

See the [Redis guide](docs/REDIS.md) for supported queries, index costs, concurrency, and deployment details.

Applications that already resolve a shared Redis database can use `new RedisLibrarianDatabase(key, storeFactory)` with a `Func<CancellationToken, ValueTask<IDatabase>>`. The caller retains connection ownership. `GetServerTime()` reads UTC time from the primary owning the namespace's hash slot using native Redis commands, without Lua.

## Query documents

The following examples use the `users` container from the quick start and this model:

```csharp
public sealed record User(string Name, int Age, bool Active);
```

### LINQ

```csharp
using System.Linq;

var adults = users.BuildQueryable<User>()
    .Where(user => user.Active && user.Age >= 18 && user.Age <= 65)
    .OrderBy(user => user.Age)
    .Take(25)
    .ToList();
```

Indexes are created automatically for supported filters and ordering, then maintained on writes. The first indexed query pays the index creation cost.

| Behavior | Memory / FileSystem | Redis | PostgreSQL |
| --- | --- | --- | --- |
| Query execution | Local, with async terminal helpers | Sync or awaited server calls | Sync or cancellable SQL calls |
| Unsupported expressions | Can fall back to local evaluation | Throw `NotSupportedException` | Throw `NotSupportedException` |
| Filtering and ordering | Indexed where supported | Must precede paging/projection | Nested paging/projection composition supported |
| Projection | Supported through LINQ | Direct fields from a bounded page | Selected fields and supported SQL computations |

Use the async terminal extensions or the explicit index methods below:

```csharp
using Soenneker.Librarian.Abstractions.Queries;

var adults = await users.BuildQueryable<User>()
    .Where(user => user.Age >= 18).OrderBy(user => user.Age)
    .Take(25).ToListAsync(cancellationToken);
```

See [query capabilities, async execution, and provider differences](docs/QUERY-CAPABILITIES.md).

### Async index queries

```csharp
await users.EnsureIndex("age");

var page = await users.FindRangeByIndex<User>(
    "age", minimum: 18, maximum: 65, skip: 0, take: 25);

var thirtyYearOlds = await users.FindByIndex<User>("age", 30, take: 10);
int count = await users.CountByIndex("age", 30);
bool exists = await users.ExistsByIndex("age", 30);

foreach (User user in page.Items)
    System.Console.WriteLine(user.Name);
```

- Use case-sensitive JSON field paths, such as `age` or `address.city`.
- Call `EnsureIndex` before explicit queries; missing indexes throw instead of scanning.
- Range bounds are inclusive; `null` means unbounded. `skip` must be nonnegative and `take` positive.
- Index values support strings, decimal-compatible numbers, booleans, and null. Missing fields are excluded.
- Results expose `Items`, `Index`, `IndexEntriesExamined`, and `DocumentsDeserialized`.

## Document operations

| Container method | Behavior |
| --- | --- |
| `AddItem(id, json)` | Adds a document; duplicate IDs throw |
| `GetItem(id)` | Returns JSON, or `null` when missing |
| `GetItemStrict(id)` | Returns JSON; missing IDs throw |
| `UpdateItem(id, json)` | Updates an existing document; returns `null` when missing |
| `UpdateItemStrict(id, json)` | Updates an existing document; missing IDs throw |
| `DeleteItem(id)` | Deletes one document |
| `GetAllItems()` / `GetAllIds()` | Returns all documents or IDs |
| `DeleteAllItems()` | Removes every document in the container |

All methods above are awaitable and accept a cancellation token. Document IDs are case-insensitive; container names are case-sensitive. The database owns container lifetime; use `UnloadContainer()` to release a container after stopping its concurrent operations.

## Atomic batches

Update related documents across containers in one all-or-nothing operation with `ILibrarianDatabase.Execute`. Add conditions to prevent overwriting concurrent changes; if a condition fails, it returns `false` and applies no writes.

Available with all four providers, including coordination across application instances with Redis and PostgreSQL. See the [atomic batch guide](docs/TRANSACTIONS.md) for an example, provider guarantees, and retry handling.

## Typed repositories

Use `LibrarianRepository<TDocument>` for serialization and typed CRUD. Models must inherit from `Document` and have a nonempty ID when added.

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Soenneker.Documents.Document;
using Soenneker.Librarian.Abstractions;
using Soenneker.Librarian.Core;

public sealed class Customer : Document
{
    public string Email { get; set; } = "";
}

public sealed class CustomerRepository(
    IConfiguration configuration,
    ILogger<LibrarianRepository<Customer>> logger,
    ILibrarianDatabase database)
    : LibrarianRepository<Customer>(configuration, logger, database, "customers")
{
}
```

Register with `builder.Services.AddScoped<ILibrarianRepository<Customer>, CustomerRepository>()`, then inject `ILibrarianRepository<Customer>`.

```csharp
await repository.AddItem(new Customer { Id = "customer-1", Email = "alex@example.com" });
Customer? customer = await repository.GetItem("customer-1");
```

Typed repositories also expose explicit index methods. Batch additions and updates execute sequentially; they are not atomic transactions.

## Performance

100,000 documents, warm indexes, .NET 10.0.12 Release. Librarian LINQ, LiteDB 5.0.21 native expression queries, and sqlite-net-pcl 1.11.285 all use in-memory storage. Query construction and execution are included. Times are medians over seven rounds.

| Query | Librarian | LiteDB | sqlite-net |
| --- | ---: | ---: | ---: |
| Equality, 10 documents | 8.983 µs | 29.341 µs | 11.866 µs |
| Range page, 10 documents | 10.337 µs | 41.159 µs | 13.113 µs |
| Count 10 matches | 2.013 µs | 29.925 µs | 3.574 µs |
| Exists, 50% hits | 2.228 µs | 22.159 µs | 2.537 µs |
| First match | 2.989 µs | 18.134 µs | 6.204 µs |
| Skip 90,000, take 10 | 8.644 µs | 53.694 ms | 1.241 ms |

Managed bytes allocated per operation:

| Query | Librarian B/op | LiteDB B/op | sqlite-net B/op |
| --- | ---: | ---: | ---: |
| Equality, 10 documents | 6,840 | 63,440 | 9,064 |
| Range page, 10 documents | 8,665 | 89,563 | 11,801 |
| Count 10 matches | 1,600 | 65,006 | 2,176 |
| Exists, 50% hits | 1,600 | 44,621 | 344 |
| First match | 1,896 | 43,887 | 4,864 |
| Skip 90,000, take 10 | 6,944 | 189,235,872 | 8,912 |

SQLite native allocations are excluded. sqlite-net uses its synchronous expression API except for existence checks, which use parameterized SQL `SELECT EXISTS`. These measurements cover warm reads; index construction, writes, persistence, and concurrency are outside the comparison.
