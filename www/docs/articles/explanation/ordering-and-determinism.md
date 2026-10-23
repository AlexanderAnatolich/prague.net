---
title: Ordering and Determinism
description: When Prague promises the order of a result and when it does not — eager, prepared and frozen execution, Sort and SortBounded, PreserveEagerOrder, the insertion order of Many indices and the order of join rows — each claim pinned by a test.
---

# Ordering and Determinism

Prague returns rows in *some* order on every path, and on most paths that order is reproducible enough to
be mistaken for a promise. This page separates the two: what the engine **guarantees** about the sequence
of a result, what merely **falls out of the mechanism** today, and how to ask for an order when you need
one. Every claim below is a region of
[`samples/Prague.Samples/Explanation/Ordering.cs`](https://github.com/nanov/prague.net/blob/main/samples/Prague.Samples/Explanation/Ordering.cs)
followed by the assertion that pins it: a guaranteed sequence is asserted as a sequence, an unspecified
one as a set.

The one-line version: **the only ordered result is a sorted one with a total comparer.** Everything else
is a set that happens to arrive in a sequence.

## What order does an eager query produce?

The eager builder has no planner; the order falls out of two mechanisms described in
[How Prague works](architecture.md): the first lane *fills* the candidate set in the order its index
enumerates, every later lane *removes* survivors in place without reordering them, and the store read
walks the survivors in slot order.

### A Many index enumerates in insertion order — mostly

A Many bucket is a `PooledSet`: a slot array walked from slot 0 to the last used slot. New keys append,
so the bucket reads back in insertion order:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#many-index-insertion-order)]

"Mostly", because a removal frees its slot and the next insertion reuses it (the free list is LIFO), so a
removed-then-added key lands where the hole was, not at the end. Under a live writer that moves rows
between buckets, insertion order is therefore not something to build on:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#slot-reuse-breaks-insertion-order)]

### The first lane decides the sequence

Later lanes only delete. Two queries with the same lanes in a different order return the same rows in
different sequences — here the customer bucket is in insertion order while `ORD-001`, which moved to
`Pending` last, sits at the end of the `Pending` bucket:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#first-lane-decides-the-order)]

This is also why the [performance advice](../advanced/performance-tuning.md) to put the most selective
lane first changes the sequence of an unsorted result: the plan and the order are the same decision.

### A Range lane walks in key order

The B+ tree enumerates ascending by index key regardless of when rows were inserted. The order inside a
run of equal keys is unspecified:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#range-lane-ascending)]

### No lane means the store's order

A `Where`-only query (and an `Or` placed before any lane) enumerates the store's hash table. The sequence is
whatever the table's bucket layout is; it is stable while the table is unchanged and changes when it
resizes, so read it as a set:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#no-lane-store-order)]

## What does `Sort` guarantee?

`Sort(comparer)` and `SortBounded(comparer)` are the two sorted plans (classic full sort versus a top-k
selection of the page; [Sorted Paging](../core-concepts/sorted-paging.md) says when to use which). Both
are **stable**: rows the comparer calls equal keep their encounter order, which is the base walk's order
from the section above. The classic plan gets this from `StableSort`; the bounded plan stamps each row with
its encounter ordinal and breaks ties on it. So for the same query the two plans return the same rows in
the same sequence:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#by-status-comparer)]

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#sort-is-stable)]

Stability is what makes **pages partition the result**: consecutive `skip` / `take` windows of an unchanged
result cover every row exactly once, and their concatenation is the whole:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#pages-partition)]

With a **total** comparer — one that never returns `0` for two distinct rows, like the generated
`OrderCache.ByCreatedAtComparer` whose `OrderId` tiebreak follows `CreatedAtUnixMs` — stability no longer
matters: the sequence is a function of the rows alone, on every path. That is the only ordering Prague
promises outright, and it is the one to use for anything a user or a downstream system will compare.

## What changes with `Prepare()`?

Nothing. `Build()` records the chain and replays it into the eager core on each execution, so a prepared
query produces the eager spelling's rows in the eager spelling's order — including the first-lane rule
above:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#prepared-replays-eager)]

## What changes with `BuildFrozen()`?

The order of an **unsorted** result is no longer promised, and the order **inside a tie group** of a
sorted one is not either. Everything else is unchanged: the same rows, the same `Count`, `TotalCount` and
`Truncated`, pages that are slices of the same whole, and a sorted result with a total comparer that is
byte-identical to eager. This is the ordering contract of `FrozenOptions`, decided so the pipeline could
seed from the *narrowest* lane for each execution's arguments instead of the first declared one — the
freedom that pays for the 3–36× in [Prepared and Frozen Queries](../core-concepts/prepared-queries.md).

So the rows of an unsorted frozen result are the eager rows, as a set:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#frozen-unsorted-no-order)]

In a sorted frozen result the comparer still decides the groups; the free seed changes the encounter
ordinals the bounded container breaks ties on, so a group's internal order can differ from eager:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#frozen-ties-unspecified)]

A total comparer removes the groups and with them the freedom — eager and frozen agree row for row, on
`Sort` and `SortBounded` alike:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#frozen-total-comparer)]

### `PreserveEagerOrder`

When the eager sequence itself is the requirement — a golden test, a snapshot diffed row by row — set
`FrozenOptions.PreserveEagerOrder`. It pins the seed to the first declared lane, walks the store for an
`Or`-first plan the way eager does, and replays the one join shape whose fused form regroups rows (an inner
`JoinOne` driven by a symmetric Many index on the left). The result is byte-identical to eager, at the cost
of the plans the flag forbids — measured on the benchmark shapes, the pinned `list ∩ list` runs at 7.6 µs
where the free seed runs at 1.3 µs:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#preserve-eager-order)]

`Count` ignores the flag; its rows carry no order.

## In what order do join rows arrive?

A `JoinOne` resolver fills a slot on each row the base walk produced and creates no rows of its own, so a
joined result keeps the base walk's sequence — the same first-lane rule, with a `Right` attached:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#join-rows-follow-the-base-walk)]

Two exceptions are documented rather than promised: an *inner* `JoinOne` driven by a symmetric Many index
on the left emits its rows grouped by right key on the eager path (its fan-out creates them per bucket),
and a frozen chain that fuses it keeps the seed's order instead — the shape `PreserveEagerOrder` sends back
to the replay.

A `JoinMany` slot — the `QueryResults<TRight>` you read as `row.Right` — is a set. The eager resolver fills
it in the order the distinct rights were first recorded, the frozen fused fill in the bucket's order; with
rights shared across lefts the two differ, and neither is a contract:

[!code-csharp[](../../../../samples/Prague.Samples/Explanation/Ordering.cs#join-many-slot-is-a-set)]

## Summary

| Result | Sequence | Guaranteed? |
|---|---|---|
| eager or `Build()`, unsorted, one or more lanes | first lane's enumeration order; survivors in place | no — mechanism, not contract |
| eager or `Build()`, unsorted, no lane | store hash order | no |
| `BuildFrozen()`, unsorted | the seed lane's order | no — explicitly unspecified |
| `Sort` / `SortBounded`, comparer with ties | comparer order, ties in encounter order (eager) or unspecified (frozen) | the groups yes, the ties only on the eager path |
| `Sort` / `SortBounded`, total comparer | comparer order | **yes**, on every path |
| `BuildFrozen(PreserveEagerOrder = true)` | the eager sequence | yes, at a cost |
| `JoinWith{T}` rows | the base walk's order | as the base walk |
| a `JoinMany` slot | bucket or pair-set order | no — read it as a set |

## Where to go next

- [Sorted Paging](../core-concepts/sorted-paging.md) — choosing between `Sort` and `SortBounded`.
- [Prepared and Frozen Queries](../core-concepts/prepared-queries.md) — `Explain()`, the seed rule, `FrozenOptions`.
- [The concurrency contract](concurrency.md) — why even a stable sequence can change between two executions.
