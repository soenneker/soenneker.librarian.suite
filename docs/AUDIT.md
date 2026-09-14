# Librarian audit — 2026-09-14

The audit covered the five projects, public contracts and registrars, shared container/repository code, explicit and automatic indexes, LINQ translation/execution, memory and filesystem ownership, persistence, tests, packaging, and build/publish workflows. The main priority was ordinary query construction and enumeration. This is a source audit with local runtime verification, not a proof that arbitrary LINQ expressions are indexed or that performance cannot improve further.

## Findings fixed

| Area | Finding | Resolution |
| --- | --- | --- |
| Secondary ordering | Replacing `OrderBy` with a materialized indexed page broke `ThenBy` execution. | Preserve the original ordering chain when secondary ordering is present; index any supported leading filter and let LINQ perform the complete sort. |
| Composed filters | A second `Where` stopped index planning and could deserialize tens of thousands of rows before taking a page. | Merge compatible predicates on the same property, including filters after ordering. Restore planner state when a predicate cannot be translated. Never move a filter ahead of paging. |
| Projections | A normal `Select` caused expression rewriting and dynamic compilation on every enumeration. | Execute common `Select`, `Where`, `Skip`, and `Take` sequence operations directly. Cache property getter delegates and weakly cache interpreted expressions, preserving live captured variables. |
| Unindexed/fallback enumeration | `Take(10)` and residual filters eagerly deserialized the entire container while holding the mutation lock. | Capture immutable raw ID/JSON snapshots under `AsyncLock`, then deserialize only consumed documents outside the lock. Reuse the raw snapshot until a write invalidates it. Returned document instances remain detached. |
| Index eligibility | A C# field-backed custom getter could be mistaken for an ordinary auto-property and indexed despite mutable behavior. | Only index non-virtual compiler-generated scalar property getters. Computed getters use fallback execution. |
| Query provider contract | Non-generic `CreateQuery` assumed the expression type's first generic argument was the element type and lacked input validation. | Resolve the actual `IQueryable<T>` contract and validate expressions. |
| Provider parity | Filesystem container-name and cached-lookup cancellation validation differed from memory behavior. | Reject blank names consistently and honor cancellation before filesystem cache access. Validate direct container construction. |
| Release workflow | Commit-derived release notes were interpolated directly into shell source and used a fixed environment-file delimiter. | Pass notes through an environment value and a notes file; generate a unique environment delimiter. The workflow was inspected locally, not executed against GitHub. |

## Ordinary LINQ measurements

100,000 documents; .NET 10.0.12 x64 Release; AMD Ryzen Threadripper PRO 9995WX. The query execution cases below use warmed indexes or a warmed raw snapshot. Five-round medians; raw files and the executable harness are in `benchmarks/Soenneker.Librarian.Benchmarks`.

| Operation | Before | After | Allocated bytes before → after |
| --- | ---: | ---: | ---: |
| Cached `BuildQueryable<Row>()` | 0.020 µs | 0.021 µs | 0 → 0 |
| Construct `Where/OrderBy/Skip/Take` | 3.107 µs | 2.983 µs | 2,936 → 2,784 |
| Fresh indexed ten-row `Select` | 214.736 µs | 10.556 µs | 18,747 → 8,689 |
| Reused indexed ten-row `Select` | 185.395 µs | 6.177 µs | 14,642 → 4,680 |
| Unindexed `Take(10)` | 89,834.963 µs | 9.130 µs | 43,208,547 → 5,560 |
| Modulo predicate followed by `Take(10)` | 89,934.847 µs | 20.900 µs | 43,215,773 → 12,670 |
| Two compatible `Where` calls and ten-row page | 44,258.863 µs | 14.513 µs | 21,624,706 → 8,710 |

The scan improvements remove unnecessary deserialization; they do not make arbitrary filters use an index. Early termination helps `Take`/`First`, while a full scan or sort must still consume its input. Query construction continues to allocate ordinary LINQ expression nodes.

Two separately labeled **single cold samples**, not medians: creating the first raw scan snapshot took 6.88 ms and allocated 1,605,656 bytes; building an additional automatic string index over 100,000 documents took 259.13 ms and allocated 60,459,760 bytes. The latter includes transient deserialized documents and index construction, not just retained index memory. Warm results must not be substituted for those costs.

## Verification

- 42 tests passed in Release, including 960 deterministic query-shape comparisons against LINQ-to-Objects with sequence, Count, and Any checks.
- Regression coverage includes secondary sorting, split predicates, paging boundaries, rollback of unsupported predicate translation, projections, captured values, custom getters, invalid/null JSON, immutable scan snapshots, mutation invalidation, detached results, provider contracts, and zero-allocation warm root creation.
- Existing randomized index mutation/rank tests, concurrent index/query/write tests, registrar lifetime tests, filesystem reload tests, atomic write failure/cancellation/retry tests, concurrent save tests, and disposal flush tests passed.
- All five NuGet packages packed successfully. Inspected package dependencies confirm `Memory → Core → Abstractions` and `FileSystem → Core → Abstractions`; neither provider depends on the other.
- The standard LiteDB comparison was rerun separately; its raw results remain in `results.csv`. The ordinary LINQ audit cases are in `audit-before.csv` and `audit-after.csv`.

Reproduce:

```sh
dotnet test --project test/Soenneker.Librarian.Suite.Tests --configuration Release -- --treenode-filter '/*/*/*/*'
dotnet run --project benchmarks/Soenneker.Librarian.Benchmarks -c Release -- --audit
dotnet run --project benchmarks/Soenneker.Librarian.Benchmarks -c Release
```

## Remaining boundaries

- Automatic indexes currently support direct `int`, `long`, `decimal`, `bool`, and string auto-properties. Equality and numeric bounds now combine across properties; supported leading AND conditions can narrow residual predicates. Secondary sorting, nested properties, nullable values, custom comparers, and other unsupported expressions can use fallback execution. Some complex operator/projection combinations still use the general LINQ provider path.
- Automatic indexes requested together share one scan/deserialization pass. Indexes and cached query roots are per container/type. Multiple automatic indexes of the same type share deserialization during writes.
- Raw scan snapshots retain about 16 bytes per document in array entries, plus existing string references, on this x64 runtime. A mutation invalidates the cached snapshot. An active enumerator can retain an older snapshot until released. No mutable typed result cache is shared between callers.
- Queries execute synchronously. First-index creation and index maintenance still serialize access through `AsyncLock`; this audit does not measure contention throughput. Stop container operations before unload/disposal, as the public lifetime contract requires.
- Filesystem storage loads the database file when opening containers and rewrites the JSON database on save. It is designed for one owner per file. This audit did not change that storage model or benchmark disk I/O.
- Runtime expression compilation/interpretation and reflection remain in use. NativeAOT/trimming support and live GitHub workflow execution were not validated.

Follow-up query planner work adds multi-index planning, residual filtering, and projected rank paging. The current suite has 48 passing Release tests; the original audit-stage measurements above remain preserved. See [query planning](QUERY-PLANNING.md) for the added behavior and measurements.
