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
- **D1 / R2:** coordinate within one snapshot owner and persist the complete JSON snapshot before publication. Batches include pending ordinary writes in loaded containers. They do not coordinate separate instances; use one owner per D1 logical name or R2 object key. A transport failure after dispatch can leave the commit outcome unknown.
- **FileSystem:** stages changes and atomically replaces the database file before publishing them to readers. Successful batches persist immediately, independent of periodic saves, and include pending changes in loaded containers. The database file requires a single owner. File data is flushed; power-loss durability still depends on filesystem and OS behavior.
- **Redis:** commits documents and indexes together using native conditional transactions, including across independent instances. Reads and queries continue to execute directly against Redis through `Soenneker.Redis.Client`; no Lua or periodic save is used. Conflicts retry up to 128 attempts before a timeout.
- **PostgreSQL:** checks conditions and commits documents, JSONB, and scalar indexes in one server transaction. A row lock serializes writes within a logical database key across instances; ordinary reads and SQL queries use statement snapshots without that lock. Failed validation or SQL operations roll back the transaction. See [PostgreSQL details](POSTGRES.md).

## Reads, cancellation, and retries

Separate read calls do not form a snapshot. Cancellation or a connection failure after dispatch can leave the caller uncertain whether a commit succeeded; reconcile using document state before retrying. Redis keys must be exclusively managed by Librarian: Redis transactions do not roll back commands on server execution errors caused by corrupted keys or incompatible key types.

## Batch sizing

Memory and FileSystem batches prepare only changed documents and their explicit and automatic index keys. Validation and automatic property extraction complete before publication; FileSystem also persists before publication. The shared gate protects incremental publication across all containers. Unchanged writes preserve indexes and cached scan snapshots. FileSystem still serializes and persists the whole database file, so its persistence cost grows with the database size.

Redis batches skip unchanged index values and fetch a document's old indexed fields together. Condition-only batches validate every expected raw value in one server script, without a write transaction or version increment.

## Redis Cluster and prototype migration

Redis containers share a database namespace hash slot to support cross-container transactions. This concentrates each database on one Redis Cluster slot. The readable `:containers:` key layout does not automatically migrate data from earlier layouts.

