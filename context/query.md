# Query execution

> **Read when:** working on the query builder, candidate narrowing/intersection, OR clauses, pooled results, or the query-string API.

## Builder & intersection

- `Query()` returns a fluent builder. Each `WithXxx(...)` / `UseIndex(...)` adds a candidate-narrowing lane; the runtime intersects lanes on a **stackalloc bitmap** and short-circuits on the first empty set — no further index walks, no allocations.
- Discriminators gate which operations are callable at compile time: `ExecutableQuery<TCache>` (full surface), `NonExecutableQuery<TCache>` (join filter callbacks — `WithXxx`/`UseIndex`/`Or` only, `Execute*` hidden), `NarrowOnlyQuery<TCache>` (OR branch lambdas — narrowing only). `AsNonExecutable()` (internal) swaps `Executable → NonExecutable` preserving other generics.

## Execution flavors

- `Execute()` — allocating.
- `ExecutePooled()` — returns a disposable `QueryResults<T>` the caller **must `Dispose()`**. Default for hot paths.
- `QueryResults<T>` has a **ref enumerator**: `Enumerator.Current` is `ref T`, so `foreach (ref var x in results)` mutates the backing array in place. `IEnumerator<T>.Current` stays an explicit by-value impl so the interface contract is unchanged.

## Sorted paging — bounded plans

- `Sort(comparer).Execute*(skip, take)` with a finite `take` never full-sorts. The base walk feeds `TopKSimpleResultContainer` (simple) or `TopKJoinedBaseContainer` (joined, `ExecuteCoreJoinedTop`), which pick a plan once, at `Init(maxCount)`:
  - **heap** while `K = skip + take` is below `maxCount / 4`: pooled max-heap of K rows (`TopKSelect.Push`/`DrainAscending`), O(N log K), buffer of K.
  - **collect + select** otherwise: every matched row is collected and `TopKSelect.SelectPage` introselects the page bounds, sorting only the page — O(N + take log take), buffer of N. Deep skips and half pages come out ahead of the classic full sort instead of 2× behind it.
- **Ties**: rows carry their encounter ordinal (`(value, ordinal)` tuples) and `TopKValueComparer` / `TopKPairComparer` break ties by it, so consecutive pages of an unchanged result partition it without duplicates or gaps. Unbounded `take` runs the classic full sort, which is stable as well (`StableSort`: the simple path sorts an index array with a 3-way introsort — Dutch-flag partition, equal-key blocks finished in encounter order, heapsort fallback — and permutes the rows once, parity with the BCL introsort on random keys and faster on heavy ties; the keyed joined path sorts the row structs IN PLACE with the framework sort carrying a pooled ordinal per row, then restores encounter order inside each run of equal rows by sorting the runs' ordinals (integers only) and gathering the rows once — an index sort there cost 1.5× because of the extra random hop into multi-megabyte struct rows per comparison), so `Execute()`, `Execute(0, N)` and any page concatenation agree on equal rows. A null comparer means `Comparer<T>.Default`, as for `Span<T>.Sort`; the sorter hands the bounded plans the same default. **Routing** (`TopKPlan`, `src/Prague.Core/QueryBuilders/TopKPlan.cs`): a simple page whose LENGTH (take clamped to the rows left after skip) reaches 3/5 of the base row bound (`ICandidatesExecutor.MaxBaseCount` — narrowed candidates or the cache size) runs the classic sort; a joined page only when it holds every row (`skip == 0`, `take >= bound`), since a skipped joined page still saves the join fill of the rows it leaves out. Measured on 100k rows: the collect plan crosses the classic sort at ≈0.6 of the result for distinct keys (it never wins on tie-heavy keys), the full page cost 1.4× (distinct) to 3.0× (ties) the classic sort, and the cost tracks the page length — a deep 1 000-row page at skip 99 000 stays on top-K at 0.2× the classic sort. Known, not changed: the heap → collect boundary (`K * 4 >= maxCount`) is too high — the heap loses to collect-all from ≈5–8 % of the result and to the classic sort from ≈17 % (distinct) / 8 % (ties); a boundary near 1/16 would remove that band at the price of the collect plan's N-row buffer for smaller pages.
- **Comparer plumbing**: the sorter hands its comparer out through `IJoinResolver.TryGetLeftComparer` (boxes a struct comparer once); the wrappers invoke it through a `Comparison<T>` delegate created once per query, because the JIT devirtualizes a hot delegate target while the same call through `IComparer<T>` stays an interface dispatch (1.7× on the page sort, 1.8× on the heap loop). One 64-byte delegate per bounded query — what the classic path already pays inside `Span.Sort`.
- **Inner joins, classic path**: the inner-join pre-pass opens a result slot for every candidate that has a right BEFORE the base walk applies the `Where` predicate; `JoinedResultContaier.DropUnfilledSlots` (after `ExecuteBase`, via `ValueDictionary.RetainMarked`) removes the slots the walk never filled — tracked by slot index in a pooled bitmap set in `Add`, never read off the value, because a value-type Left equal to default is real data — otherwise rejected rows surfaced with `Left = null` (or `default`) (pre-existing on main for `UseIndex + Where + InnerJoinOne`, exposed again when a full page routes to the classic core). Pinned by `SortedInnerJoinWithFilterCoreTests`.
- **Inner joins, bounded path**: the narrow pass (`UnsafeNarrowIndexedInner`) keeps the right values that passed the filter in a pooled `ValueDictionary` on the resolver; the fill (`UnsafeFillNarrowedInner`) copies them onto the page rows without re-reading the cache or re-running the filter, so a right replaced between the passes cannot surface; `ReleaseNarrowedInner` returns the map from the `finally`. Shapes that cannot be bounded (comparer over joined fields, post-join sorter, a resolver without `SupportsNarrowOnly`) run the classic pipeline unchanged.
- **Leak safety of BuildResults**: the classic containers mark the rented result buffer handed off only after the sort and the clone succeeded, so a comparer or `Clone()` that throws inside `BuildResults` leaves the buffer (and, for joins, the values array and the Many-buffer disposer) to `Dispose`. Pinned by `Leaks/ClassicSortCloneLeakTests`.
- `TopKSelect` is zero-allocation and reaches elements by reference; `ValueDictionary` is fixed-capacity and its insert paths throw `InvalidOperationException` on overrun in every build configuration.
- Canonical refs: `benchmarks/Prague.Benchmarks/TopKExecuteBenchmarks.cs` + `RESULTS.MD`, `tests/Prague.Core.Tests/Join/TopK*`, `tests/Prague.Core.Tests/Collections/TopKSelectTests.cs`.

## OR clause — disjunctive narrowing

- `.Or(b1, b2)` and `.Or(b1, b2, arg)` — UNION-style candidate narrowing. Branch lambdas receive a `NarrowOnlyQuery<TCache>` discriminator.
- Protocol: bitmap mark-and-prune via `IncrementalIntersecter` ([`collections.md`](collections.md)); cross-branch UNION via `BitHelper` SIMD `Vector<int>` ops; survivors pruned by `RetainOnly`. **Cost scales with surviving candidates, not index-result size.** No-op branches (`q => q`) are detected and excluded.
- Works inside `JoinOne` filter callbacks (paired core uses a hybrid: `IncrementalIntersecter` per-branch, `ValueSet` merge cross-branch — pairs dedup by `.Key`). Orchestrated by `IOrCapable` on both the unpaired and paired cores.
- Canonical refs: README "OR Queries", `docs/superpowers/specs/2026-05-19-or-query-clause-design.md`.

## Query-string API

Codegen emits `TryApplyParam`, `ApplyFilter`, `StringQueryInternal` per cache (string-keyed dynamic filtering), all using the `ExecutableQuery<{cacheClassName}>` discriminator.

## Related

Index lanes & bulk-intersect primitives: [`indexes.md`](indexes.md). Joins build on the paired variant of this builder: [`joins.md`](joins.md).
