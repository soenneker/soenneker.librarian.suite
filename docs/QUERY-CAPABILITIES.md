# Query capabilities and asynchronous execution

Memory and FileSystem share the Core query engine. They use indexes for recognized query prefixes and evaluate supported remaining operators with an AOT-safe local executor. Typed operations require [generated JSON contracts](native-aot.md). Redis and PostgreSQL execute supported filters/paging on their servers and reject unsupported expressions. A shared interface does not make every LINQ expression portable.

| Capability | Memory / FileSystem | Redis | PostgreSQL |
| --- | --- | --- | --- |
| Scalar comparisons, boolean predicates | Yes | Yes | Yes |
| Multiple ordering keys | Local LINQ | No | SQL |
| Ordinal prefix searches | Local LINQ | Indexed range | Indexed range |
| Ordinal substring/suffix searches | Local LINQ | No | Server index scan |
| Captured array/list/default-comparer set membership | Local LINQ | Equality-set union | SQL `ANY` |
| `Count`, `LongCount`, `Any`, `All`, first/single variants | Yes | Yes | Yes |
| Projections | Local LINQ, with indexed scalar-page optimizations | Direct scalar/constructor/DTO projections of a bounded page | Selected JSONB fields and supported scalar expressions |
| Numeric `Sum`, `Average`, `Min`, `Max` | Local LINQ | No | SQL |
| Filtering/ordering after paging | Local LINQ | No | Nested SQL stages |
| Filtering/ordering after projection | Local LINQ | No | Scalar, anonymous-type, and direct DTO member mappings |
| Scalar `Distinct` | Local LINQ | No | Strings, booleans, supported numeric types and nullable variants |
| Arithmetic, numeric field-to-field comparisons, string length | Local LINQ | No | Supported scalar expressions in SQL |
| Joins and grouping | Use `AsEnumerable()` explicitly | No | No |
| Method calls inside local predicates/projections | Interpreted expressions (no ref structs) | Only supported server operations | Only supported server operations |
| Async terminal helpers | Cancellable local work | Awaited Redis operations | Cancellable Npgsql operations |

See [PostgreSQL details](POSTGRES.md) and [Redis details](REDIS.md) for exact overloads, costs, and restrictions. Explicit-default terminal overloads and custom comparer overloads are not generally portable to remote providers.

## Async query execution

```csharp
using Soenneker.Librarian.Abstractions.Queries;

var query = container.BuildQueryable<Order>()
    .Where(order => order.Paid).OrderBy(order => order.Amount).Take(50);

var orders = await query.ToListAsync(cancellationToken);
var first = await query.FirstOrDefaultAsync(cancellationToken);
int count = await query.CountAsync(cancellationToken);
bool any = await query.AnyAsync(cancellationToken);

// Any supported terminal expression can use the generic helper.
decimal total = await query.ExecuteAsync(q => q.Sum(order => order.Amount), cancellationToken);
```

The extensions are in `Soenneker.Librarian.Abstractions.Queries`. They include `ToListAsync`, `ToArrayAsync`, `CountAsync`, `LongCountAsync`, `AnyAsync`, `AllAsync`, `FirstAsync`, `FirstOrDefaultAsync`, `SingleAsync`, `SingleOrDefaultAsync`, and `ExecuteAsync`. Predicate overloads exist for `CountAsync`, `AnyAsync`, `AllAsync`, and `FirstAsync`; other predicates can be expressed with `Where` or `ExecuteAsync`. Provider expression restrictions still apply.

All built-in query providers implement `ILibrarianAsyncQueryProvider`. Third-party providers must implement it to use these extensions; an ordinary `EnumerableQuery` or unrelated provider throws rather than being silently wrapped in `Task.Run`.

PostgreSQL passes cancellation to connection opening, index creation, command execution, and row reading. Redis checks cancellation between operations and retries, awaits commands already dispatched, and cleans up temporary query keys before returning. Memory/FileSystem await local locks and check cancellation while building indexes, scanning, and collecting results; CPU work stays on the caller's thread. Arbitrary local user code is not forcibly interrupted. Synchronous enumeration remains available and blocks for remote I/O. Obtaining a queryable asynchronously from a repository alone does not make later enumeration asynchronous.

## Semantics that differ

- **Missing properties:** explicit scalar indexes exclude missing properties and include explicit JSON null on every provider. Memory/FileSystem LINQ deserializes CLR defaults and initializers, so a missing nullable/string property normally behaves like null. Remote indexed equality to null matches only explicit JSON null. Remote negated equality includes missing properties.
- **String comparison:** explicit indexes use ordinal UTF-16 ordering. Remote string predicates also use ordinal rules. Local LINQ overloads retain .NET behavior, including culture-sensitive ordering/searches; specify ordinal behavior when comparing results across providers.
- **Ties:** indexed numeric pages use document IDs as tie-breakers. Redis now persists value-plus-ID sort fields to make these pages deterministic. Redis's escaped normalized ID representation can order punctuation/non-ASCII IDs differently from the other providers. Supply an explicit unique sort field when portable ordering matters.
- **Ordering missing values:** remote indexed ordering excludes documents missing any required ordering path. Local typed queries can order their CLR default values.
- **Numbers:** explicit indexes share exact decimal-compatible scalar keys. PostgreSQL arithmetic and aggregates use SQL numeric types and checked result conversion; decimal division and averaging retain at least 28 fractional digits before conversion to CLR decimal. Floating-point accumulation and overflow timing can differ from local LINQ.
- **Custom projections:** PostgreSQL fetches JSONB fields, and Redis extracts selected fields from a bounded raw document page. Isolated scalar deserialization uses shared serializer options, not property-level converters or source-object initializers. Arbitrary CLR getters and constructors cannot be translated as SQL computations.
- **Snapshots:** a remote SQL query sees one statement snapshot; Redis compound queries validate container versions. Separate queries, even awaited back-to-back, are not a shared snapshot. Cross-container atomic changes belong in conditional batches.

## Core optimizations

Simple scalar projections followed by `Count`, `LongCount`, or `Any` can now use the source index without deserializing projected documents. Async scalar paging moves `Skip`/`Take` ahead of eligible automatic-property projections so only the selected document page is deserialized. Arbitrary selectors and computed getters retain their ordinary evaluation path.

`QueryConformanceTests` runs common behavior against all four providers, verifies explicit-index null/precision/ordinal rules, asserts documented LINQ differences, and checks cancellation, lifetime, and deserialization costs. PostgreSQL composition/cancellation tests and Redis sort-upgrade tests cover provider-specific behavior.
