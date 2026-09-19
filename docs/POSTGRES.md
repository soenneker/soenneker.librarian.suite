# PostgreSQL provider

`Soenneker.Librarian.Postgres` implements `ILibrarianDatabase` and `ILibrarianContainer` using Npgsql and PostgreSQL 16 or later. Documents and scalar indexes persist immediately. Filters, ordering, paging, counts, existence checks, and first/single operations execute in SQL. Unsupported LINQ expressions throw `NotSupportedException`.

## Registration

```csharp
using Soenneker.Librarian.Postgres.Registrars;

builder.Services.AddPostgresLibrarianDatabaseAsSingleton();
// AddPostgresLibrarianDatabaseAsScoped() is also available.
```

```json
{
  "Librarian": {
    "Postgres": {
      "ConnectionString": "Host=localhost;Database=app;Username=app;Password=...",
      "Key": "my-application"
    }
  }
}
```

Use your application's secret configuration for credentials. `Key` isolates a logical database within the physical PostgreSQL database. Multiple instances using the same key share documents and indexes; different keys are isolated. Container names are case-sensitive, and document IDs are case-insensitive.

For direct construction:

```csharp
await using var database = new PostgresLibrarianDatabase(connectionString, "my-application");
var users = await database.GetContainer("users");
```

For an existing connection pool, use `new PostgresLibrarianDatabase(dataSource, key)`. The caller owns that `NpgsqlDataSource`; disposing the provider does not dispose it. The connection-string and configuration constructors own their pools.

## SQL queries

```csharp
await users.EnsureIndex("age");
var page = await users.FindRangeByIndex<User>("age", minimum: 18, skip: 0, take: 25);
int count = await users.CountByIndex("age", 30);
bool exists = await users.ExistsByIndex("age", 30);

var adults = users.BuildQueryable<User>()
    .Where(user => user.Age >= 18 && user.Active)
    .OrderByDescending(user => user.Age)
    .Skip(25).Take(25).ToList();
```

LINQ supports these operations in SQL:

| Operation | Supported forms |
| --- | --- |
| Filters | Scalar property comparisons (`==`, `!=`, `<`, `<=`, `>`, `>=`), boolean AND/OR/NOT, constant true/false |
| String searches | `StartsWith`, `EndsWith`, and `Contains`, using ordinal comparison |
| Membership | Captured arrays, `List<T>`, or `HashSet<T>` with default/ordinal equality; inline arrays; translated to SQL `ANY` |
| Ordering | `OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending` on scalar property paths |
| Paging | Composed `Skip`/`Take`, including paging after projection |
| Projection | `Select` of scalar properties/supported computations, including constructor/member-initializer results |
| Composition | Filtering/ordering after paging; filtering/ordering/reprojection through scalar, anonymous-type, and direct DTO-member aliases |
| Distinct | Scalar string, bool, numeric and nullable projections, retaining first occurrences |
| Computations | Numeric arithmetic, negation, numeric casts/coalescing, numeric field-to-field comparisons, UTF-16 string length |
| Aggregates | `Count`, `LongCount`, `Any`, `All`; numeric `Sum`, `Average`, `Min`, `Max` with property selectors or after scalar projection |
| Elements | `First`, `FirstOrDefault`, `Single`, `SingleOrDefault`, including projected results |

Property paths respect the shared serializer's naming policy and `JsonPropertyName`. Captured scalar values and membership arrays are parameters. String searches without a comparison argument use Librarian's ordinal rules; the explicit `StringComparison.Ordinal` overload is also supported. Culture-sensitive and case-insensitive overloads throw. Literal `%`, `_`, backslashes, and UTF-16 code-unit boundaries are preserved. Null/missing strings do not match string searches. Membership containing null matches explicit JSON null, not missing properties.

Filtering or reordering after paging introduces a nested SQL stage, so `Take(10).Where(...)` filters those ten rows. Each stage retains sequence information for subsequent paging and final output ordering. Projections can come before or after paging and can be composed when their aliases map directly to source expressions. Scalar projections, anonymous types, and direct DTO member assignments can be used in later filters/orderings; arbitrary constructor-derived members are not inferred. SQL returns only selected fields; local code constructs result objects. Property-level custom converters are not applied to isolated projected scalars; they use shared serializer options.

Numeric `+`, `-`, `*`, `/`, `%`, negation, numeric conversions, numeric `??`, and `string.Length` can be used in predicates/projections/aggregate selectors. Ordering keys must still be scalar property paths. Length counts UTF-16 code units, including two for supplementary characters. Computed expressions read JSONB and can require a server scan. Nulls propagate through SQL calculations; missing projected scalars use CLR defaults. Scalar `Distinct` uses a server window over the scalar value and keeps the first occurrence, so preceding paging is preserved.

Numeric aggregates support `int`, `long`, `float`, `double`, `decimal`, and nullable variants. They read numeric JSONB fields and respect any preceding paging. Missing fields contribute zero for nonnullable selectors and null for nullable selectors. Nullable aggregates ignore nulls. `Sum` returns zero for empty/all-null sequences; the other numeric aggregates return null for nullable results or throw for empty nonnullable results. Aggregate results are converted to the LINQ return type, with overflow checked on integral/decimal conversion. SQL numeric accumulation and floating-point rounding follow PostgreSQL semantics.

```csharp
var names = new[] { "Alex", "Sam" };
var page = users.BuildQueryable<User>()
    .Where(user => names.Contains(user.Name) || user.Name.StartsWith("Jo"))
    .OrderBy(user => user.Age).ThenBy(user => user.Name)
    .Skip(10).Take(20)
    .Select(user => new { user.Name, user.Age }).ToList();

decimal total = orders.BuildQueryable<Order>()
    .Where(order => order.Paid).Sum(order => order.Amount);
```

`GroupBy`, joins, `SelectMany`, object/multi-column `Distinct`, other set operations, arbitrary method calls, date-part extraction, document-array predicates, comparer overloads, and explicit-default element overloads remain unsupported. These throw rather than downloading documents for local evaluation.

Queries are deferred and read current server state on each execution. LINQ enumeration is synchronous by default; use the async terminal extensions in Soenneker.Librarian.Abstractions.Queries or explicit async index methods when cancellation is needed. SQL selects document IDs for the page before fetching document bodies or projected fields. Only returned results are deserialized. Aggregates return a scalar and never transfer document bodies to the client. `Any` limits its SQL subquery to one match; `All` looks for at most one failing match. PostgreSQL's planner chooses the physical scan and join strategy.

Prefix searches use an indexed scalar range. Suffix and substring searches may scan the selected field's index entries on the server. Numeric aggregates and projections read JSONB directly and do not create indexes solely for their selected fields; ordinary filters and ordering still use persistent scalar indexes. Multiple sort keys can require a server sort because the storage index covers one property path at a time.

Indexes use a shared B-tree on `(database_key, container, path, value, id_key)`. Scalar keys preserve exact decimal ordering and ordinal UTF-16 string ordering under PostgreSQL's `C` collation. Values sort as null, boolean, number, then string. Missing properties have no index entry; explicit JSON null does. As with Redis, negated equality includes missing properties. Ordering on an indexed path excludes documents missing that path.

Explicit index methods require `EnsureIndex`. LINQ creates the indexes it needs automatically. First-time index creation scans existing raw JSON in bounded pages and validates scalar values before committing the definition and entries together. Later queries use persisted entries; writes maintain them transactionally. Creating an index blocks writes to that logical database until construction completes, so pre-create indexes before latency-sensitive traffic.

`IndexEntriesExamined` reports matching entries returned to the client, not PostgreSQL's physical work. SQL `OFFSET` can visit skipped index entries on the server; large offsets therefore cost more than small offsets.

## Storage and transactions

The provider creates these storage tables in `public` on its first database operation:

- `librarian_postgres_databases`: logical database write coordination.
- `librarian_postgres_documents`: original ID, exact document text, and JSONB body.
- `librarian_postgres_indexes`: persistent index definitions.
- `librarian_postgres_values`: ordered scalar entries.

The database role needs permission to create these tables and indexes, and to read/write them. Schema creation is protected against concurrent first use. All identifiers in SQL are fixed; keys, IDs, property paths, document text, and query values are parameters.

Original text is authoritative for reads and batch comparisons. Valid PostgreSQL JSONB is stored alongside it for native inspection; raw strings and JSON outside JSONB's supported representation remain readable with a SQL-null body. Indexing malformed JSON or non-scalar values throws. PostgreSQL text cannot store literal NUL characters or invalid Unicode, and indexed keys are subject to PostgreSQL's B-tree entry-size limit. An oversized indexed value rejects the entire write or index construction transaction.

`Execute(LibrarianBatch)` checks exact-text conditions and commits all document/index changes in one PostgreSQL transaction. Ordinary mutations and index construction share the same logical database row lock, so independent instances cannot both claim a document under the same condition. Writes to different logical keys can proceed independently. Writes within one key are serialized; reads use PostgreSQL MVCC and do not acquire that write lock. Each read/query statement observes a consistent snapshot; separate reads are not a shared snapshot.

Failed conditions return false. Validation and SQL failures roll back all changes. Cancellation is checked before commit, but a cancelled or interrupted commit can have an unknown outcome; reconcile authoritative state before retrying non-idempotent work.

`Save` and `MarkDirty` are no-ops. Unloading disposes only the local handle and retains documents and indexes. Stop using containers before unloading them or disposing their database. No background persistence task or server is started by the provider. Change data only through the provider so document text, JSONB, and scalar entries stay consistent.

## Integration tests

Set `LIBRARIAN_TEST_POSTGRES` to a disposable PostgreSQL connection string, then run:

```sh
dotnet test --project test/Soenneker.Librarian.Suite.Tests -- --treenode-filter "/*/*/Postgres*/*"
dotnet test --project test/Soenneker.Librarian.Suite.Tests -- --treenode-filter "/*/*/TransactionTests/*"
```

Tests use unique logical keys and delete their own data afterward. CI starts PostgreSQL 17 and includes the provider in package creation.

Connection pooling and parameter usage follow the [Npgsql documentation](https://www.npgsql.org/doc/basic-usage.html).
