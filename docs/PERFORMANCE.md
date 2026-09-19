# Performance comparison

For incremental batch writes and the bulk read/count APIs used by Flywheel, see the [2026-09-19 integration pass](FLYWHEEL-INTEGRATION.md). The [2026-09-17 performance pass](PERFORMANCE-2026-09-17.md) covers earlier write, query, and Redis measurements. The engine comparison below is preserved from the earlier benchmark run.

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

[Benchmark source and methodology](../benchmarks/Soenneker.Librarian.Benchmarks/README.md) · [Three-engine results](../benchmarks/Soenneker.Librarian.Benchmarks/sqlite-net-results.csv).

