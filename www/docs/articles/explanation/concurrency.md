---
title: The Concurrency Contract
description: One writer, lock-free readers — what a Prague query can observe while the cache is being written, what is safe to share across threads, how writes become visible, and the mechanisms in InMemoryDataCache that make the contract hold.
---

# The Concurrency Contract

A Prague cache is written by one thread — in production the Kafka consumer — while request handlers
query it from every other thread. There is no query-time lock and no snapshot isolation, by design. The
contract that follows is short:

- **A query never fails because of a concurrent write.** No exception, no torn buffer, no use of recycled
  memory.
- **A query makes no consistency promise.** A row written mid-query may be included or missed, two legs
  of one join may disagree, and a row may be judged by an index that has not caught up with its value.
  Stale or short results are correct behaviour.
- **The next query sees the write.** A write is published before `AddOrUpdate` returns; anything that
  orders a reader after the writer orders its query after the write.

Everything below is the *why* behind those three lines, verified against `src/Prague.Core`, so you can
predict what your code will see.

## Who is allowed to write?

Exactly one thread per cache. `InMemoryDataCache.AddOrUpdate` writes the store first and then tells every
index, in registration order, with no lock spanning the steps; the index structures underneath
(`PooledSet`, the Many bucket and key-set index; `PooledBTree`, the Range index) are documented as
*single writer, lock-free readers*. The store itself is a striped-lock hash table and tolerates concurrent
writers, but the index maintenance around it does not: two threads updating the same key can apply their
index updates in the opposite order from the one the store settled on, leaving an index pointing at the
loser's value until that key is written again, and the Unique index's approximate counter is a plain
`+=`.

Nothing guards against this at run time — a second writer is not an exception, it is an unsupported
configuration. If several producers must feed one cache, serialize them yourself:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Concurrency.cs#serialize-multiple-writers)]

The Kafka layer already does this for you: `KafkaCacheConsumer` consumes on one dedicated thread, which
writes the cache itself during the initial load and then hands each cache's live messages to that cache's
single `RawLiveWorker` (`ApplyRawLiveAsync`), so one topic is one writer at any time. Calling `AddOrUpdate`
from your own code on a Kafka-fed cache makes you the second writer.

## What can a reader observe mid-write?

The reader side takes no lock. `TryGetValue` on the store reads the `volatile` table reference once and
walks the bucket chain; a `PooledSet` reader walks the slot array of the generation it saw published; a
`PooledBTree` reader descends release-published nodes. Each of these reads is *individually* safe and
*individually* stale. A query is many such reads in sequence, and the writer can run between any two of
them. Concretely:

| Moment | What the reader can see |
|---|---|
| store written, indexes not yet | the row at its new value, but still in its old bucket: an eager `WithCustomerId(42)` can return an order whose `CustomerId` now reads `7`; the new bucket does not list it yet, so `WithCustomerId(7)` misses it |
| Many index mid-`Update` | the key in both the old and the new bucket — `Update` adds to the new bucket before removing from the old, so a key is never in *no* bucket |
| Range index mid-shift | a window walk may skip an entry or see it twice while a leaf is being shifted (`PooledBTree`'s documented staleness); the eager candidate set is a set, so a double sighting cannot become a duplicate row |
| candidate key whose row was removed | nothing: the store read that turns keys into values skips a key it no longer holds |
| join legs read at different instants | the left row from before a write and its right from after it, or an outer join's `Right` as `null` for a right that exists by the time you look |

The frozen pipeline narrows the first window. Its *probe* steps — every step that is not the seed — judge a
row by the value they just fetched from the store, not by the index, so a probe never admits a row that
contradicts the query. The *seed* step is still index-judged, and the `FrozenOptions.IndexSideProbes` flag
restores the eager index-side read for the probes too. Both behaviours are inside the contract.

What you can rely on in every mode is a **self-consistent result**: `Count` equals the rows you can
enumerate, no key appears twice, and no row refers to memory the writer has recycled. The test below runs
one writer against four readers for 20,000 writes and 8,000 queries and asserts exactly those invariants
— nothing about timing:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Concurrency.cs#writer-and-readers)]

## How does a write become visible?

Three publication points, all in `src/Prague.Core`:

- **The store.** A writer takes the `Monitor` of the stripe its key hashes to, links the new node, and
  publishes the bucket head with `Volatile.Write`; the table reference itself is a `volatile` field that a
  resize swaps whole. A reader that observes the new head observes the node's key and value, because they
  were written before the publishing store.
- **`PooledSet` (Many buckets, key sets).** A slot is written value-first, then its hash code with a
  volatile store, then `LastIndex`; a reader scanning up to the `LastIndex` it read cannot land on a
  half-written slot. All reader-visible arrays belong to one `Tables` generation published with a single
  volatile store, so a reader never pairs old arrays with new bounds. The key-set index (`CacheKeySetIndex`)
  additionally takes a `lock` around its writes and its set-level reads (`Contains`, the bulk copy and
  the intersections) — the one locked reader path in the engine; its value-side `Matches` probe, which
  the frozen pipeline uses, takes none.
- **`PooledBTree` (Range).** New nodes are release-published and leaf counts are acquire-read, so a reader
  never sees a node before its contents.

Retiring memory is the other half. A grown `PooledSet` or a split `PooledBTree` node hands its old arrays to
`ReaderGate`, which keeps them in limbo until every thread that was inside a read when the retirement
happened has left (each reader thread pins a cache-line-padded slot on entry; the writer snapshots the
pinned slots and reclaims once they have all unpinned). Only then do the arrays return to the pool. That is
why a reader can never observe recycled memory, and why neither side ever blocks on the other.

Because publication is a volatile store and `AddOrUpdate` returns after it, any ordering you already have
between threads — a signal, a message on a channel, a `Task` continuation — orders the query after the
write:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Concurrency.cs#next-query-sees-the-write)]

## What is safe to share across threads?

| Object | Share it? | Why |
|---|---|---|
| `XxxCache` / `InMemoryDataCache` | yes — one writer, any readers | the whole design above |
| `PreparedQuery` / `FrozenQuery` | yes, from any thread, concurrently | immutable after `Build()` / `BuildFrozen()`; per-execution state lives on the caller's stack and in per-thread pools |
| the fluent builder (`cache.Query()…`) | no — build and execute it on one thread | it is a `struct` holding a candidate set and `stackalloc` buffers |
| `QueryResults<T>` | hand it to **one** owner; dispose exactly once, on any thread | it is a struct owning a pooled array; copies share the array |

A frozen query in a field is the normal shape of a hot path, and it is safe under a writer:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Concurrency.cs#shared-frozen-query)]

Pooled results do not care which thread disposes them — `PragueArrayPool<T>.Pool` is process-wide — only
that someone does, once. A result can be produced on a request thread and consumed on another:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Concurrency.cs#dispose-on-another-thread)]

`Dispose()` nulls the array reference inside the struct you call it on, so disposing the *same variable*
twice is harmless; a second copy of the struct taken earlier still points at the array. Keep one owner.
`ToArray()`, `ToList()` and `Map()` dispose the source for you; the rows a non-cloned result contains are the
cache's own instances, so treat them as read-only or use `ExecutePooledCloned()`.

## What about joins?

A join reads two caches at two instants, so its legs can disagree — that is the "torn join" the contract
allows. The engineering concern is different: a `JoinMany` slot is a fixed partition of one shared buffer
and cannot grow, so a resolver that sized a slot from a live index count and then delivered rows from a
second read of the same index could be handed more rows than it reserved. Two mechanisms close that:

1. **Slots are sized from the pairs the resolver recorded, not from a live count.** Every `JoinMany`
   resolver first enumerates each left's right bucket once into its pair set and then calls `Init(left,
   pairsRecorded)`; removals and filters can only reduce what is delivered, so the reservation is an
   exact upper bound.
2. **`QueryResults.UnsafeAdd` drops rather than throws.** Should a slot still fill up, the row is dropped,
   `QueryResults<T>.Truncated` is set and `QueryResultsDiagnostics.DroppedRows` is incremented — the floor
   under every resolver, present and future. In `DEBUG` builds the drop throws so a sizing defect fails a
   test.

`DroppedRows` is expected to stay `0`; a non-zero value is a bug report. Under a writer churning the right
side, the stable rows are always present and `Truncated` stays `false`:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Concurrency.cs#join-slots-under-a-writer)]

## Does the contract say anything about order?

Only that it says nothing. An eager result comes out in the first lane's order, a frozen one in its seed's,
and a sorted one with ties in an order the comparer does not determine. A concurrent writer can change all
three between two executions. When the sequence matters, say so with `Sort` / `SortBounded` and a total
comparer; the details are in [Ordering and determinism](ordering-and-determinism.md).

## Do / Don't

| Do | Don't |
|---|---|
| Write from one thread per cache; let `Prague.Kafka` be that thread | Call `AddOrUpdate` from request handlers on a Kafka-fed cache |
| Store `Build()` / `BuildFrozen()` results in fields and execute them from any thread | Share a half-built `cache.Query()…` builder between threads |
| `using var rows = …ExecutePooled();` — one owner, one `Dispose()` | Copy a `QueryResults<T>` into two places and dispose both |
| Treat a result as the set of rows live at *some* instant; re-query for the current state | Expect two legs of a join, or two queries, to agree under a writer |
| Branch on `rows.Truncated` if a short join list would be worse than a retry | Treat `Truncated == true` as normal — it is not; report it |
| Use `ExecutePooledCloned()` when you will mutate the rows | Mutate instances a non-cloned result hands you — they are the cache's |

## Where to go next

- [How Prague works](architecture.md) — the structures named above, in context.
- [Ordering and determinism](ordering-and-determinism.md) — the one guarantee this page leaves out.
- [Performance Tuning](../advanced/performance-tuning.md) — why `ExecutePooled` is the default on hot paths.
