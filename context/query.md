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
- **Ties**: rows carry their encounter ordinal (`(value, ordinal)` tuples) and `TopKValueComparer` / `TopKPairComparer` break ties by it, so consecutive pages of an unchanged result partition it without duplicates or gaps. Unbounded `take` runs the classic, unstable full sort: equal rows may order differently than in a finite page over the same rows.
- **Comparer plumbing**: the sorter hands its comparer out through `IJoinResolver.TryGetLeftComparer` (boxes a struct comparer once); the wrappers invoke it through a `Comparison<T>` delegate created once per query, because the JIT devirtualizes a hot delegate target while the same call through `IComparer<T>` stays an interface dispatch (1.7× on the page sort, 1.8× on the heap loop). **This is a per-query allocation on an otherwise zero-alloc pooled path**: measured 64 B/op with a class comparer and 88 B/op with a struct comparer (the extra 24 B is the box). The simple path's classic sort pays the same 64 B, so bounding is alloc-neutral there; the **joined** classic path pays only 24 B, so bounding it is a 24 → 64 B regression. Tracked for removal by flowing the comparer type through the chain instead of erasing it to `IComparer<T>`.
- **Inner joins**: the narrow pass (`UnsafeNarrowIndexedInner`) keeps the right values that passed the filter in a pooled `ValueDictionary` on the resolver; the fill (`UnsafeFillNarrowedInner`) copies them onto the page rows without re-reading the cache or re-running the filter, so a right replaced between the passes cannot surface; `ReleaseNarrowedInner` returns the map from the `finally`. Shapes that cannot be bounded (comparer over joined fields, post-join sorter, a resolver without `SupportsNarrowOnly`) run the classic pipeline unchanged.
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
