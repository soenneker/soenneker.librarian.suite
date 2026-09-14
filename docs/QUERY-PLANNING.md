# Query planning

This guide covers the Memory and FileSystem providers. See the [Redis guide](REDIS.md#query-support) for server query support and restrictions.

Use normal LINQ; indexes are internal:

```csharp
var names = users.BuildQueryable<User>()
    .Where(user => user.Active && user.Age >= 18 && user.Age <= 65)
    .OrderBy(user => user.Age)
    .Skip(20)
    .Take(10)
    .Select(user => user.Name)
    .ToList();
```

The planner compares index cardinalities. For sparse filters it starts with the smallest candidate set and checks the other properties through their indexes. For broad filters and a small ordered page, it can traverse the ordering index and stop when the page is full. If sorting is necessary, it sorts matching IDs using index keys, then loads only the returned documents. This is a cost estimate, not a guarantee that correlated predicates always receive the cheapest possible plan.

## Supported optimizations

- Conjunctions across direct scalar auto-properties, whether written in one `Where` or several. Boolean property predicates (`user.Active` and `!user.Active`) and reversed comparisons (`18 <= user.Age`) are recognized.
- Filtering on one property and ordering on another, without deserializing the candidates merely to sort them.
- Multi-index `Count` and `Any` operate on keys. `Any` stops after enough matches to satisfy the existing page.
- Direct auto-property projections can move across paging: `Select(user => user.Age).Skip(90000).Take(10)` can seek to the page before loading documents.
- Computed projections stream documents instead of eagerly materializing all indexed candidates. The planner preserves evaluation of computed expressions before `Skip` when required.
- Unsupported residual predicates retain supported leading AND conditions. For example, `Active && Name.StartsWith("A")` can narrow by the active index, then evaluate the name check lazily. Paging is applied after that residual check.
- Multiple indexes first requested together are built using one document-deserialization pass. Existing empty indexes can short-circuit a query without building additional indexes.

The planner does not move filters across an existing page, narrow an OR expression to one branch, reorder potentially throwing residual expressions, or drop secondary ordering. These cases retain ordinary LINQ behavior.

## Verified document reads

Tests count document constructors to verify materialization, not just elapsed time. On 2,000 stored documents, after index warmup:

| Query | Documents deserialized |
| --- | ---: |
| Three-property filter, ordered three-row page | 3 |
| Filter and order on different properties, three-row page | 3 |
| Property projection, skip 1,900, take 3 | 3 |
| Indexed residual predicate, take 3 | 7 candidates to find 3 matches |
| Multi-index Count/Any | 0 |

The cold three-index count deserializes the 2,000 documents once, not once per index. The test suite has 48 passing Release tests, including 600 additional multi-property query-shape comparisons and concurrent creation/write coverage.

## Warm benchmarks

100,000 documents, .NET 10.0.12 x64 Release, in-memory provider. Five-round medians including fresh expression construction; results and source are in `benchmarks/Soenneker.Librarian.Benchmarks`.

| Query | Time | Allocated bytes |
| --- | ---: | ---: |
| Broad multi-field ordered page, 3 rows | 7.34 µs | 6,016 |
| Selective multi-field ordered page, 3 rows | 9.48 µs | 6,680 |
| Filter and order on different properties, 3 rows | 9.82 µs | 5,736 |
| Multi-field count | 4.75 µs | 3,024 |
| Indexed residual predicate, take 3 | 14.75 µs | 13,213 |
| Select property, skip 90,000, take 10 | 8.97 µs | 7,936 |

```sh
dotnet run --project benchmarks/Soenneker.Librarian.Benchmarks -c Release -- --planner
```

First index construction still scans stored documents. Weakly selective filters can require many index-key checks; residual predicates may require examining every remaining candidate. Lazy execution captures raw JSON references for a consistent snapshot, so a broad candidate set can still allocate a large reference array without deserializing all its documents. Nested/nullable properties, custom comparers, and other unsupported operations can still fall back to scans. No mutable document results are shared between callers.
