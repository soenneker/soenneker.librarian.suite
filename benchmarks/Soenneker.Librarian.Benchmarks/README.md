# LINQ comparison

Run from the repository root:

```sh
dotnet run --project benchmarks/Soenneker.Librarian.Benchmarks -c Release
```

The checked-in `results.csv` was measured on an AMD Ryzen Threadripper PRO 9995WX (96 cores, 192 logical processors), Windows 10.0.26200, .NET 10.0.12 x64. The benchmark executes synchronously on one thread, with tiered compilation disabled.

- 100,000 documents: string ID, string group (10 documents per group), unique integer score, and a 128-character payload.
- Librarian uses `BuildQueryable<Row>()` with standard LINQ. LiteDB 5.0.21 uses its native expression query API (`Where`, `OrderBy`, `Offset`, `Limit`, `Count`, and `Exists`), keeping queries inside its engine.
- All three use memory storage: Librarian's memory provider, a LiteDB `MemoryStream`, and SQLite `:memory:`. All have warm group and score indexes. Librarian creates its indexes automatically during validation; LiteDB and SQLite indexes are created explicitly.
- sqlite-net-pcl 1.11.285 uses its synchronous expression API for equality, range, count, first-match, and paging. Existence uses a parameterized SQL `SELECT EXISTS` through sqlite-net because its table query API has no equivalent existence operator. SQLite maps the string ID as a primary key.
- Every measured operation builds a fresh query and executes it. Pages materialize complete documents into lists. Query construction, expression translation, deserialization, and list allocations are included.
- Equality and bounded range queries vary over 100 values. Exists alternates between hits and misses. Deep paging skips 90,000 documents and returns 10.
- IDs and scalar results are checked against an independent expected dataset before timing.
- 200 warmup calls per engine per case, then seven rounds of 2,000 operations (100 for deep paging). Engine order rotates each round. Results report median time and median allocated bytes per operation using `Stopwatch` and `GC.GetAllocatedBytesForCurrentThread`.

Allocation figures cover managed allocations on the current thread only; SQLite native allocations are excluded.

`sqlite-net-results.csv` records the three-engine comparison. The earlier `results.csv` is preserved as the two-engine optimization baseline. Run with `-- --validate` to check all three engines without timing.

These are warm read microbenchmarks. Initial index construction, writes, disk persistence, and concurrent workloads are outside this measurement. Unsupported LINQ expressions that fall back to scanning are not represented.

LiteDB plans are saved in `plans.txt`. Equality uses a group index seek; bounded ranges use a score index scan with a residual upper-bound filter; deep paging uses a full score index scan with an offset of 90,000. The deep-page result therefore includes LiteDB's indexed offset traversal, while Librarian seeks by index rank. Inspect plans with:

```sh
dotnet run --project benchmarks/Soenneker.Librarian.Benchmarks -c Release -- --plans
```

## Optimization comparison

`before-optimization.csv` preserves the prior run; `results.csv` contains the updated implementation. Count dropped from 5.232 to 1.853 µs and Exists from 5.171 to 1.871 µs. Both dropped from 5,352 to 1,552 allocated bytes per operation. Page latency was essentially unchanged (including a small deep-page increase); page allocations decreased modestly. First-match measurements were added in the updated run and have no recorded pre-change baseline.

The changes remove synthetic expression-tree construction for indexed scalar operations, avoid Task/queryable/result wrappers on direct indexed execution, cache query roots and property metadata, and use the equality count lookup directly. Page deserialization occurs outside the mutation lock. Writes deserialize once per document type when maintaining multiple automatic indexes; this behavior is covered by tests, but writes are not timed by this harness.

## Ordinary LINQ audit

Run with `-- --audit` for root creation, query composition, fresh/reused projections, unindexed paging, fallback filters, and split predicates. `audit-before.csv` and `audit-after.csv` preserve the comparison. These cases use five rounds with case-specific iteration counts (see `AuditBenchmarks.cs`). Scan cases warm the immutable raw snapshot; index cases warm their automatic indexes. Cold samples are labeled separately and are single observations. The root benchmark consumes its cached object's hash code; the composition benchmark creates a fresh expression chain without enumerating it. The final harness also validates projected values and page IDs before warm timing. See `../../docs/AUDIT.md` for findings and limits.

## Multi-index planner

Run with `-- --planner` to measure multiple-property conjunctions, cross-property ordering, metadata-only counts, residual predicates, and property projection before deep paging. `planner-results.csv` records five-round medians with 2,000 iterations per round. The harness validates actual page values before timing. Indexes are warm, query construction is included, and these cases are Librarian planner measurements rather than LiteDB comparisons. See `../../docs/QUERY-PLANNING.md` for document-deserialization counts and planner limits.

