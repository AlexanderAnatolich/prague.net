---
title: How Prague Works
description: The path from a [DataCache] attribute to a query result — what the source generator emits, how InMemoryDataCache stores rows and indexes them, how a query is planned and executed, and where every byte is allocated or pooled.
---

# How Prague Works

Prague is a library you compile against, not a server you talk to. Everything a query does is decided
in three places: at **compile time** by the source generator, at **build time** of a prepared query by
the planner, and at **run time** by a handful of pooled data structures that one writer mutates while
any number of readers walk them without a lock. This page follows one entity through all three so you
can predict what a given call will do, what it will cost, and what it will not promise.

![The layers of Prague: a [DataCache] POCO, the Prague.Codegen source generator, the generated XxxCache partial with its index fields and fluent Query() and Prepare() surface, the InMemoryDataCache store with its Unique, Many, Range and key-set indexes, the eager and frozen executors, and the pooled QueryResults handed back to the caller](../../images/architecture.svg)

## What does `[DataCache]` turn into?

`Prague.Codegen` is a Roslyn incremental generator (`CacheGenerator`, `netstandard2.0`) that runs inside
your compiler. For every `partial class` marked `[DataCache]` it reads the attributes on the properties
and emits C# into your assembly. Nothing is reflected at run time; everything a query can do is a method
that exists in your binary and that the compiler checked.

For the `Order` entity used across these docs it writes, per type:

| File | Contents |
|---|---|
| `Prague_Samples_Models_Order.g.cs` | `partial class OrderCache` and `static class OrderCacheQueryExtensions` (below) |
| `…Order.Equality.g.cs` | `partial class Order : ICacheEquatable<Order>` — `CacheEquals`, `CacheGetHashCode`, `CacheEqualityComparer`, structural and deep, honouring `[DataCacheIgnoreEquality]` |
| `…Order.Clone.g.cs` | `partial class Order : ICacheClonable<Order>, IDataCacheItem<string, Order>` — `Clone()` and the `__PragueMetadata__` (Kafka timestamp and offset) slot |

and once per assembly: `CacheEqualityHelper_{Assembly}` and `CacheCloneHelper_{Assembly}` (the bodies the
partials delegate to), a `DataCachesResolver` for `System.Text.Json`, the `Add{Xxx}Cache(…)` builder
methods for `AddKafkaCaches`, and one `.GlobalIndex.g.cs` per `IDataCacheGlobalLastUpdateIndex` type.

The `OrderCache` partial holds:

- `Cache` — the `InMemoryDataCache<string, Order>` that owns the rows. Everything else is a view on it.
- One **public readonly index field per attribute**, typed by the index implementation:
  `CreatedAtUnixMsIndex : CacheRangeIndex<…>`, `StatusIndex : CacheKeyValueListIndex<…>`,
  `CustomerIdIndex : CacheSymmetricKeyValueListIndex<…>` (the `ManyToOne` foreign key upgrades to the
  symmetric variant so the forward join can read its `Reverse`), `HasSettledAtUnixMsIndex : CacheKeySetIndex<…>`.
  The constructor creates each one through `Cache.CacheRangeIndex(static (key, doc) => doc.CreatedAtUnixMs)`
  and friends — a `static` lambda, so the selector is one delegate for the life of the cache.
- `AddOrUpdate` / `Remove` / `TryGet` forwarders that pull the key out of the document.
- `Query()`, `Prepare()` and `Prepare<TArgs>()`, each returning a builder whose first type argument — the
  *discriminator* — names `OrderCache`.
- `ByCreatedAtComparer`, one `IComparer<Order>` per `[DataCacheSort]`.
- `AddJoinedCaches(...)` and `AddGlobalIndexes(...)`, which the `DataCacheRegistryBuilder` and
  `AddKafkaCaches` call for you.

`OrderCacheQueryExtensions` is where the fluent surface lives: `WithCustomerId(int)`, `WithStatus(…)`,
`WithCreatedAtUnixMs(q => q.Gte(…))`, `WithSettledAtUnixMs()` / `WithoutSettledAtUnixMs()`, `WithKey`,
`WithOrderId`, and `JoinWithCustomer()` / `InnerJoinWithCustomer()` / `JoinWithOrderLine()` /
`JoinWithShipment()` in three filter flavours each. Every one is an extension method constrained on
`ICacheCarrier<OrderCache>`, which is why `orderCache.Query().WithCustomerId(…)` compiles and
`customerCache.Query().WithCustomerId(…)` is a compile error (CS0315), not a runtime one. The prepared twins
(`OrderCachePreparedQueryExtensions`) take a `Func<TArgs, T>` where the eager method takes a value.

To read the generated code, turn on the compiler's file emission in the project that holds the entities:

```xml
<PropertyGroup>
  <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
  <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)Generated</CompilerGeneratedFilesOutputPath>
</PropertyGroup>
```

The files land under `obj/Generated/Prague.Codegen/Prague.Codegen.CacheGenerator/` (without the second
property, under `obj/<Configuration>/<TargetFramework>/generated/…`). They are regenerated on every build;
never edit them.

## Where do the rows live?

`InMemoryDataCache<TKey, TValue>` is two things: a `ConcurrentCacheStore<TKey, TValue>` and an array of
`ICacheIndex<TKey, TValue>`.

The store is a striped-lock hash table in the `ConcurrentDictionary` family: writers take the lock of the
stripe their key hashes to and publish the new bucket head with a `Volatile.Write`; readers take no lock —
`TryGetValue` reads the `volatile` table reference once and walks the chain. The store is what `TryGet`
hits, what `WithKey` resolves against, and what every query finally reads its values from.

A write runs in a fixed order. `AddOrUpdate(key, value, timestampMs)` first asks the store to swap the
value, passing `!old.CacheEquals(new)` as the condition — that is the generated structural equality, and
it is why a replay of an identical Kafka message is reported as `Same`, touches no index and returns
`false`. Otherwise the statistics collector is told, and every index in the array is told `Add(key, …)`
or `Update(key, old, new, …)` in the order the indexes were registered. `Remove` does the same in reverse:
store first, then every index. There is no lock across those steps, which is the root of the
[concurrency contract](concurrency.md).

## What is an index, concretely?

Each attribute maps to one class; each class is built on one of the internal pooled collections. Knowing
the shape tells you the cost of `WithXxx` before you measure it.

| Attribute | Index class | Backing structure |
|---|---|---|
| `[DataCacheIndex(Unique)]` | `CacheUniqueIndex` | a `ConcurrentCacheStore<TIndexKey, TKey>` (one hash lookup) |
| `[DataCacheIndex(Many)]` | `CacheKeyValueListIndex` | a `ConcurrentCacheStore<TIndexKey, PooledSet<TKey>>` — one bucket per value |
| `[DataCacheIndex(Range)]` | `CacheRangeIndex` | a `PooledBTree<TIndexKey, TKey>` — a B+ tree with 64-slot leaves on rented arrays |
| `[DataCacheHasValueIndex]`, `[DataCacheValueIndex]`, … | `CacheKeySetIndex` | one `PooledSet<TKey>` of the keys whose value passes the predicate |
| `[DataCacheGlobalLastUpdateIndex]` | `LastUpdatedIndex` | a store of `(timestamp, count)` per group plus a `CacheRangeIndex` over the timestamps |

`Symmetric = true` (and every `ManyToOne` / non-key `OneToOne` foreign key) adds a **reverse** unique index
from primary key back to index value, so a join can go from an `Order` to its `CustomerId` in O(1) without
reading the order's value. A Unique index updates by adding the new key first and then removing the old one
*only if it still points at this entity* — two entities swapping keys cannot drop a live mapping. A Many
index's `Update` likewise adds to the new bucket before removing from the old, so a key may be briefly
visible in two buckets but never in none.

### The collections underneath

These are `internal`, but their behaviour is what you observe:

- **`PooledSet<T>`** — the Many bucket and the key-set index. A free-list hash set on `ArrayPool` arrays
  with **one writer and lock-free readers**: all reader-visible state is one `Tables` generation published
  with a volatile store, a slot is published value-first then hash then `LastIndex`, and a retired
  generation's arrays go back to the pool only after every reader that could have seen them has left
  (`ReaderGate`, an RCU-style grace period with one padded pin slot per thread). Enumeration is slot order,
  which is insertion order until a removal frees a slot and a later add reuses it.
- **`PooledBTree<TIndex, TValue>`** — the Range index. Writes are serialized by the tree's own lock; reads
  are lock-free with the same grace-period reclamation. Strictly ascending keys (timestamps) take an O(1)
  append into the last leaf. `EstimateCount` gives the frozen planner a cardinality signal for a window
  without walking it.
- **`ValueSet<T>`** — the **candidate set** of a running query. A `struct` with 47 entries of inline storage
  (no allocation for small results) and pooled arrays beyond; `Dispose()` returns them. Its nested
  `IncrementalIntersecter` is the intersection engine: a bitmap over the set's slots, `stackalloc`'d at 100
  `int`s (3,200 slots) and rented above that, where each later lane *marks* the candidates it still
  contains and `Dispose` *sweeps* the unmarked ones. Nothing is copied, nothing is re-hashed.
- **`ValueDictionary<TKey, TValue>`** — the keyed row map a joined or sorted query materializes into. Its
  values array is the hand-off payload that becomes the result; its `Dispose()` returns only the metadata
  for that reason.
- **`JoinedKeyPair<TLeft, TKey>`** — the element of a join's pair set; equality is by right key alone, so
  intersection is by key and many lefts sharing one right collapse to one pair.
- **`PragueArrayPool<T>.Pool`** — the single pool hook every rental goes through. In production it is
  `ArrayPool<T>.Shared`; the test suite swaps in a tracking pool and asserts every scenario returns what it
  rented, exceptions included.

## How is a query planned?

An eager query has no optimizer. The call order **is** the plan.

![Candidate intersection: the first lane fills the ValueSet from its index bucket, each later lane marks the survivors on a stackalloc bitmap and the sweep drops the rest, an empty lane short-circuits, Or unions two branch bitmaps, and Where runs on the rows fetched from the store](../../images/query-intersection.svg)

1. **The first `WithXxx` seeds the candidate set.** A Unique lane adds zero or one key; a Many lane adds
   its bucket's keys in bucket order; a Range lane walks the tree between the bounds and adds in key
   order; a key-set lane copies its set. Keys are added to a `ValueSet<TKey>`, so duplicates cannot exist.
2. **Every later lane intersects.** An `IncrementalIntersecter` is opened over the candidates, the lane
   marks each candidate it contains (for a Unique or Many lane this is a hash probe per candidate or per
   bucket key — whichever side is being walked; a Range lane walks the window), and the sweep removes the
   unmarked slots in place. Removals leave holes the enumerator skips, so **the survivors stay in the
   first lane's order**.
3. **An empty set short-circuits.** When a lane leaves nothing marked the intersecter is `Cleared`; the
   remaining lanes return immediately and `Execute*` hands back `QueryResults<T>.Empty` without reading
   the store.
4. **`Or(b1, b2)` is a union of marks.** Each branch gets its own intersecter and bitmap (two `stackalloc`
   budgets side by side); the branches run as narrow-only builders; their bitmaps are OR-ed with
   `Vector<int>` ops and the union is what survives. An `Or` placed before any lane seeds the candidates
   from the whole store first — expensive, and the reason the frozen planner treats `Or`-first differently.
5. **No lane at all means a store walk.** `Where`-only queries enumerate the store's hash table.
6. **`Where` runs on fetched rows.** Candidates are keys. The store read that turns them into values
   (`TryGet(ref container, ref candidates, predicate)`) applies the predicate as each value is fetched,
   and a key whose row has meanwhile been removed is simply skipped.
7. **Resolvers run last.** `Sort` / `SortBounded` and every `JoinWith{T}` are resolvers appended to the
   chain by type; they see the rows the base walk produced.

![Join resolution: for each left row a JoinOne resolver follows the foreign key — through a unique index, the symmetric index's Reverse map or primary key to primary key — into the right store and fills the row's slot; a JoinMany resolver records the (left, right) pairs from the right-side bucket, sizes each slot from what it recorded, then fills the slots from one shared buffer](../../images/join-resolution.svg)

A **join** is a resolver that reads a second cache. `JoinOne` has four families chosen at compile time by
what the foreign key declares — primary key to primary key, a Unique index on the right, a symmetric Unique
index on the left, or a symmetric Many index on the left — and each is one or two hash lookups per row.
`JoinMany` records, for each left, the right keys in its bucket, sizes the left's slot from what it
recorded, and then performs one store lookup per *distinct* right; a right shared by many lefts is fetched
once and delivered to all of them. `InnerJoinWith{T}` runs the same walk and then drops the rows whose slot
stayed empty. The full family table is in [Joins](../core-concepts/joins.md).

## How is a query executed?

Four terminals, one data structure.

| Call | What happens | Dispose |
|---|---|---|
| `Execute()` / `ExecuteCloned()` | plan and run now; results in a fresh `T[]` | not needed (a no-op) |
| `ExecutePooled()` / `ExecutePooledCloned()` | plan and run now; results in an `ArrayPool` rental | **required**, exactly once |
| `Count()` | the same lanes, then a key-presence walk — no values are copied | — |
| `Prepare()…Build()` → `Execute(in args)` | the chain is recorded once (`PreparedQuery<TArgs, TResult>`, 88 B); each execution replays it into a stack-local eager core | as above, per execution |
| `Prepare()…BuildFrozen()` | the chain is recorded *and planned* once; an executor built for that shape runs each time | as above |

The `Cloned` variants call the generated `Clone()` on every row so you can mutate the copy; the plain ones
hand you the cache's own instances — treat them as read-only.

### The three frozen executors

![Frozen executor selection: BuildFrozen flattens the chain into a plan; a unique-equality step followed only by filters binds the PointLookup executor; every other shape the pipeline planner accepts — index steps, Or / If / Match composites, Sort or SortBounded, fusable joins — binds the Pipeline executor, which seeds from the narrowest step and probes the rest per key; everything else binds Replay, the prepared query's own execution](../../images/frozen-executors.svg)

`BuildFrozen()` flattens the recorded chain into descriptors and binds one of three executors
(`FrozenPlanner` in `FrozenQuery.cs`); `Explain()` prints which:

- **`PointLookup`** — the first step is a Unique-index equality and everything after it is a `Where`.
  Execution is two probes (index, then store) and the filters: 18–27 ns against ~130 ns eager on the
  benchmark machine.
- **`Pipeline`** — every other *simple* plan the `PipelinePlanner` accepts (Unique, Many, Range, key-set and
  last-updated steps, `Or` / `If` / `Match` over them, with or without `Sort` / `SortBounded`), and every
  *joined* plan whose joins fuse — a `JoinOne` with no filter callback or one the build-time probe reduced
  to a pure value predicate, and every `JoinMany`. Per execution it binds the arguments into each step,
  **picks a seed** — by default the step with the smallest cardinality signal for *these* arguments
  (a Unique step's 0/1 beats everything; a Many bucket's live count and a Range window's tree estimate
  compete, the estimate winning only when twice it is still smaller) — copies the seed's keys out once
  under a single `ReaderGate` pin into a 1 KB stack buffer (pooled above), and then **probes** every other
  step per key: on the key where the index can answer, otherwise on the value it fetches from the store.
  There is no candidate set and no intersection; `Where` runs inline; the result container is the eager
  one. Fused joins do their right lookup inside the same pass, per row.
- **`Replay`** — everything else: a `JoinOne` whose filter the probe could not reduce, nested joins, two
  sorters, a filter-only plan, a marking step that is not first in an `Or` branch. It is exactly what
  `Build()` does, plus the fused-filter and capacity-hint optimizations when they apply.

The one thing the pipeline gives up is the eager row sequence — the free seed walks a different lane.
That is a deliberate contract, explained in [Ordering and determinism](ordering-and-determinism.md); the
`FrozenOptions.PreserveEagerOrder` flag buys the sequence back at the cost of the fixed seed.

## What comes back?

`QueryResults<T>` is a `readonly struct` over an array segment: `Count`, `TotalCount` (the pre-paging
count), `Truncated`, an indexer, `AsSpan()`, and a `ref` enumerator — `foreach (ref var row in rows)` edits
the backing array in place. Whether the array is pooled is recorded inside the struct.

**Ownership is the whole story.** A pooled result owns one rented array plus, for joins, a
`QueryResultsDisposer` holding the child buffers — a `JoinMany` fills every left's slot from one shared
buffer partitioned by the recorded pair counts, and the slot you read as `row.Right` (a
`QueryResults<TRight>`) is a window into it. One `Dispose()` on the outer result returns everything.
`Dispose()` writes through the `readonly` fields to null the array reference and zero the disposer's
count, so disposing the same variable twice is harmless — but a *copy* of the struct taken before the
dispose still holds the array, which is why the rule is "one owner, one `Dispose()`", not "dispose
defensively". `ToArray()`, `ToList()` and `Map()` consume the result (they dispose it for you);
`ToPooledArray()` hands you the rental itself.

Inside a join the same discipline appears as the `handedOff` guard: a resolver's pair set is disposed by
whoever ends up owning it — the resolver if a user filter throws before execution, the paired read
otherwise — because a double return to `ArrayPool` is swallowed by `ValueSet.Dispose()` but corrupts the
pool (the same buffer can be rented twice). Forgetting a `Dispose()` on your side leaks a rental until the
GC collects it; the pool simply allocates a fresh array next time.

## Memory and pooling

Every hot path is written to a fixed budget: inline or `stackalloc` storage up to a threshold, a
`PragueArrayPool<T>` rental above it, a heap allocation only where something must outlive the call.

| Structure | Inline / stack budget | Above it |
|---|---|---|
| `ValueSet<T>` candidates | 47 inline entries | pooled arrays |
| intersection bitmap (`IncrementalIntersecter`) | 100 `int`s on the stack (3,200 slots), ×2 for `Or` | `PragueArrayPool<int>` |
| frozen pipeline seed keys | 128 `long`s on the stack (1 KB) | `PragueArrayPool` |
| `JoinManyFanOut` pair set | ~1 KB inline | pooled |
| `QueryResultsDisposer` child buffers | 2 inline entries | an array sized to the chain's `JoinMany` count |

What a call allocates, measured with `[MemoryDiagnoser]` on an Apple M4 Pro, .NET 9 (every body
`ExecutePooled` + `Dispose` unless noted; `benchmarks/Prague.Benchmarks/RESULTS.MD`, sections
"Prepared queries — eager builder vs `PreparedQuery<TArgs, TResult>`", "Frozen queries — `BuildFrozen()`
stage 1" and "Optimimizations Rounds 3 - Final"):

| Operation | Allocated per call | Note |
|---|---:|---|
| Unique-index lookup, eager / prepared / frozen | 0 B | `Unique_*` rows |
| Many bucket + constant `Where`, eager / prepared / frozen | 0 B | `ListWhere_*` (the `1 B` cells are BDN amortizing a one-off pool refill) |
| `Where(v => v.Id >= a.min)` capturing a per-call value, eager | 89 B | the closure and its delegate |
| the same `Where` as `static (v, in a) => …` on a prepared / frozen query | 0 B | the argument is bound into a per-thread pooled predicate box |
| `Build()` of a four-step chain | 88 B, once | the `PreparedQuery` object |
| `SortBounded` page of 20, eager / prepared | 0 B | `SortBounded_*`; struct or class comparer |
| `JoinOne`, eager / prepared / frozen | 0 B | `JoinOne_*` (`5 B` amortized refill) |
| `Or` over two 1k buckets in a 100k store, eager | 3,136 B | the branch bitmaps outgrow the stack budget |
| every frozen `Pipeline` shape, `ExecutePooled` and `Count` | 0 B | pinned by `FrozenPipelineAllocationTests` |
| `Execute()` instead of `ExecutePooled()` under 11 readers for 3 s | 4.8 GB vs 90 KB total | `ConcurrentReadsWithWriter_NotPooled` vs `_Pooled`: a `T[]` per call, Gen0 607k vs 0 |

The last row is the reason the docs say "always `ExecutePooled` on a hot path": the pooled and the
allocating variants do the same work, but one of them hands the GC a new array per request.

## Where to go next

- [The concurrency contract](concurrency.md) — what the one-writer, lock-free-reader design lets a query
  observe mid-write.
- [Ordering and determinism](ordering-and-determinism.md) — when the row sequence is promised.
- [Query Engine](../core-concepts/query-engine.md), [Index Types](../core-concepts/index-types.md),
  [Joins](../core-concepts/joins.md), [Prepared and Frozen Queries](../core-concepts/prepared-queries.md)
  — the how-to pages for each layer.
