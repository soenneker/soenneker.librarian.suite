# Atomic batches

[Back to the README](../README.md#atomic-batches)

## Example

All built-in providers implement `ILibrarianDatabase.Execute` for atomic writes across documents and containers:

```csharp
using Soenneker.Librarian.Abstractions.Transactions;

var jobs = await database.GetContainer("jobs");
string? pending = await jobs.GetItem("job-1");
if (pending is not null)
{
    bool claimed = await database.Execute(new LibrarianBatch(
        writes:
        [
            new LibrarianWrite("jobs", "job-1", "{\"status\":\"running\",\"revision\":2}"),
            new LibrarianWrite("claims", "job-1", "{\"worker\":\"worker-1\"}")
        ],
        conditions:
        [
            new LibrarianCondition("jobs", "job-1", pending),
            new LibrarianCondition("claims", "job-1", null)
        ]));
}
```

## Conditions and writes

Conditions compare exact raw text, including JSON whitespace and property order. A null expected value requires a missing document. A null write value deletes; other writes upsert. Each document can have one condition and one write. Failed conditions return `false` without writes; validation failures throw before publication. Include a revision or fencing value in documents when a change back to identical text must be detected.

## Provider guarantees

- **Memory:** conditions and publication share a database-wide gate with ordinary operations. Guarantees apply within one database instance.
- **FileSystem:** stages changes and atomically replaces the database file before publishing them to readers. Successful batches persist immediately, independent of periodic saves, and include pending changes in loaded containers. The database file requires a single owner. File data is flushed; power-loss durability still depends on filesystem and OS behavior.
- **Redis:** commits documents and indexes together using native conditional transactions, including across independent instances. Reads and queries continue to execute directly against Redis through `Soenneker.Redis.Client`; no Lua or periodic save is used. Conflicts retry up to 128 attempts before a timeout.
- **PostgreSQL:** checks conditions and commits documents, JSONB, and scalar indexes in one server transaction. A row lock serializes writes within a logical database key across instances; ordinary reads and SQL queries use statement snapshots without that lock. Failed validation or SQL operations roll back the transaction. See [PostgreSQL details](POSTGRES.md).

## Reads, cancellation, and retries

Separate read calls do not form a snapshot. Cancellation or a connection failure after dispatch can leave the caller uncertain whether a commit succeeded; reconcile using document state before retrying. Redis keys must be exclusively managed by Librarian: Redis transactions do not roll back commands on server execution errors caused by corrupted keys or incompatible key types.

## Batch sizing

Memory and FileSystem batches clone changed containers and rebuild their explicit indexes before publication; automatic indexes rebuild lazily. FileSystem persists the whole database file. Account for these costs when sizing batches.

## Redis Cluster and prototype migration

Redis containers share a database namespace hash slot to support cross-container transactions. This concentrates each database on one Redis Cluster slot. The readable `:containers:` key layout does not automatically migrate data from earlier layouts.

