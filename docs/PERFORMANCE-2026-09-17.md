# Performance and allocation pass — 2026-09-17

Compared the implementation at `06ee034` with this change, using the same final benchmark sources in both checkouts. Measurements use .NET 10.0.12 x64 Release on Windows 10.0.26200, 192 logical processors, with tiered compilation disabled. Each table reports five-round medians. Raw before/after CSVs are in [benchmark results](../benchmarks/Soenneker.Librarian.Benchmarks/results).

## Measured changes

Memory operations use 10,000 documents. Times include the operation and exclude initial database/document setup. Allocation measurements are managed bytes on the calling thread.

| Operation | Before | After | Bytes/op before → after |
| --- | ---: | ---: | ---: |
| Get one raw document | 0.029 µs | 0.032 µs | 0 → 0 |
| Update without indexes | 0.081 µs | 0.064 µs | 0 → 0 |
| Get all raw values | 66.730 µs | 21.488 µs | 160,104 → 80,056 |
| Get all IDs | 54.916 µs | 16.214 µs | 160,104 → 80,056 |
| Capture persistence snapshot | 259.519 µs | 87.383 µs | 582,520 → 400,056 |
| Update with two automatic indexes | 1.861 µs | 1.511 µs | 144 → 120 |
| Prepare/commit batch with three explicit indexes | 34.949 ms | 16.771 ms | 11,387,137 → 9,182,850 |
| Same batch plus rebuilding a numeric automatic index | 55.596 ms | 24.970 ms | 16,482,463 → 14,070,593 |

Warm LINQ queries use 100,000 documents and include fresh expression construction except for the explicitly reused query.

| Operation | Before | After | Bytes/op before → after |
| --- | ---: | ---: | ---: |
| Broad multi-field ordered page, 3 rows | 6.882 µs | 5.887 µs | 6,016 → 5,200 |
| Selective multi-field ordered page, 3 rows | 10.915 µs | 9.141 µs | 6,680 → 6,136 |
| Multi-field count | 4.498 µs | 3.855 µs | 3,024 → 2,376 |
| Indexed residual predicate, 3 rows | 13.822 µs | 12.787 µs | 13,213 → 11,525 |
| Unindexed take, 10 rows | 10.203 µs | 8.750 µs | 5,696 → 5,328 |
| Fresh indexed projection, 10 rows | 10.277 µs | 10.367 µs | 8,833 → 8,697 |
| Reused indexed projection, 10 rows | 6.115 µs | 6.265 µs | 4,824 → 4,688 |

The small projection time increases and single-document read difference are reported rather than presented as speedups. These microbenchmarks run on a shared machine; nanosecond differences are not reliable evidence of a throughput change. Allocation and command counts are stronger evidence for the reductions. Cold samples in `audit-*.csv` are single observations, not medians, and are excluded from these tables.

Redis integration measurements use 100 documents, warm indexes, and an existing local Redis server in WSL. Allocation figures cover the whole benchmark process because async work moves between threads; they exclude Redis server memory and are not directly comparable with calling-thread allocation figures above. Command counts come from the client's operation counter and include transaction protocol overhead.

| Operation | Before | After | Process bytes/op before → after | Commands/op before → after |
| --- | ---: | ---: | ---: | ---: |
| Equality count | 758.283 µs | 254.318 µs | 12,068 → 3,717 | 12 → 2 |
| Equality page, 10 rows | 861.033 µs | 326.387 µs | 16,197 → 8,334 | 12 → 2 |
| Update payload with two unchanged indexed fields | 969.932 µs | 864.404 µs | 36,300 → 11,276 | 31 → 14 |

Pure Redis key encoding, without network I/O:

| Operation | Before | After | Bytes/op before → after |
| --- | ---: | ---: | ---: |
| Decimal key encoding, preboxed input | 1.340 µs | 0.117 µs | 734 → 144 |
| Escape an already safe key segment | 0.028 µs | 0.011 µs | 48 → 0 |

The new decimal encoder allocates only its returned 59-character string. A caller passing an unboxed number to an `object` parameter still pays for that boxing; the encoding benchmark deliberately excludes it.

## Implementation

- Core document storage uses `Dictionary` under the existing mutation gate. The redundant concurrent dictionary previously added synchronization and copied its keys/values before the public methods copied them again. All document access still holds the gate; cached query roots retain their concurrent dictionary because root creation happens outside it. Unordered enumeration order remains unspecified.
- Persistence snapshots allocate their final list capacity once. Batch preparation copies documents once and parses each document once across all explicit indexes. Memory batches pass their existing typed container dictionary instead of copying it for every execution.
- Index insertion discovers linked-list neighbors during tree insertion instead of traversing twice. Numeric and boolean automatic index getters produce scalar keys directly without boxing.
- Short query chains, filter backups, and multi-index working arrays use inline buffers. Longer expressions fall back to owned arrays. Filter lookups avoid capturing delegates. Candidate enumeration is a struct; ordered streaming pages copy directly into their pooled result buffer instead of building an intermediate ID list. Buffers are returned on empty intersections and exceptions.
- Redis caches immutable container key strings and query roots, reuses safe key segments, and traverses JSON property paths with spans. Primitive index keys bypass temporary JSON documents. Decimal encoding uses the exact existing fixed-width representation without `BigInteger` temporaries.
- Redis equality filters use persistent buckets. Single-set reads use atomic Redis `SCARD`/`SORT`; composed queries retain version validation and temporary-set expiry protection. Single-input set combinations reuse their input. Existing schema checks still go to Redis, so this introduces no schema or document cache. Writes skip index maintenance only when the old and new encoded text is identical.

## Verification and limits

All **80 Release tests passed, with no skips**, including live Redis tests, randomized query/index comparisons, concurrent mutations and queries, atomic batches, filesystem write failure/retry, cancellation, reload and disposal. Added coverage exercises long query chains, more than eight simultaneous filters, empty pooled intersections, live closures, primitive encodings, and **5,836 decimal cases** across every scale against the prior mathematical storage format. An existing filesystem package downgrade was corrected by aligning `Soenneker.Utils.File` to the version already required by Core's JSON dependency.

This is a measured improvement within the existing contracts, not proof of an absolute performance ceiling. The remaining major costs are substantive:

- Fresh LINQ expressions allocate framework expression nodes; reusable query objects avoid their reconstruction. Result objects are deserialized separately for each caller to preserve isolation and mutable captured values remain live.
- Atomic batches still copy container state and rebuild explicit indexes before publication. Incremental transactional indexes would require a different rollback/publication design.
- Full JSON file saves still read/serialize the database, and the shared mutation gate serializes operations. Disk throughput and sustained contention were not benchmarked by this pass.
- Redis still pays network/protocol costs and checks index existence. Multi-filter ranges and cross-field ordering retain their existing set-based execution. Pipelining broader reads or moving transaction/query logic into server scripts would be separate architectural work.

No public API or persisted encoding was changed. No webserver was started. Benchmarks clean up their unique Redis namespaces and leave the caller-owned Redis server running.
