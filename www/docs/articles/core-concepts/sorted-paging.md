---
title: Sorted Paging
description: Sort versus SortBounded in Prague queries - when each plan wins, how ties are broken, allocation behaviour and measured timings.
---

# Sorted Paging

Sorted queries come in two execution plans, and you choose one. Nothing is inferred from the
arguments: the right plan depends on how much of the matched result you are about to take, which
only you know. Both accept any `IComparer<T>`, including the `XxxCache.{Name}Comparer` fields
emitted from `[DataCacheSort]`. The blocks below run against a 120-order sample cache and are
asserted in `samples/Prague.Samples/Concepts/SortedPaging.cs`.

[!code-csharp[](../../../../samples/Prague.Samples/Concepts/SortedPaging.cs#sort-vs-sort-bounded)]

## Which one

| You are… | Use | Why |
|---|---|---|
| paging — `take` is a small slice of what matched (tens to a few hundred rows) | `SortBounded` | 2.3–8.6× faster; `skip` is irrelevant, so deep pages win too |
| taking a large chunk — `take` covers roughly a quarter or more of what matched | `Sort` | `SortBounded` is 1.3–2.7× slower there: no sort left to skip, only overhead |
| calling `Execute*()` with no page at all | `Sort` | unbounded always runs the classic sort anyway |

The deciding variable is **`take` as a fraction of the matched rows** — not `skip`, and not the cache
size.

## Why the two differ

`Sort` materialises every matched row and sorts all of them, then slices the page out. Cost is
`O(N log N)` regardless of how few rows you asked for.

`SortBounded` never sorts what you did not ask for. With a finite `take` it picks a plan once, from
`K = skip + take` against the number of candidates:

- **`K` below a quarter of the candidates** — a pooled max-heap of `K` rows. `O(N log K)`, and the
  buffer is sized to `K`, not to the result.
- **otherwise** — collect the candidates, then introselect the page boundaries and sort only the
  page. `O(N + take log take)`.

It buffers rows as `(value, ordinal)` pairs — twice the bytes of a bare reference — and earns that
back by skipping the sort of everything outside the page. Ask for a page and the saving dwarfs the
overhead. Ask for most of the result and only the overhead is left, which is why a near-full `take`
is *slower* than `Sort` and is the one shape where the plan allocates (an `O(N)` collect buffer).

Chain shapes the bounded plan cannot handle — a comparer over joined fields, a sorter applied after a
join, an inner join that cannot narrow — fall back to the classic pipeline automatically, with
identical results.

## Ties

A comparer that returns `0` for two distinct rows leaves nothing to chance here: **both plans are
stable**. `Sort` keeps comparer-equal rows in encounter order; `SortBounded` carries each row's
encounter ordinal and breaks ties by it. The two plans therefore return the same rows in the same
order for the same query, and consecutive pages of an unchanged result partition it with no
duplicates and no gaps — under either plan. The sample sorts by `Status`, which ties thirty ways:

[!code-csharp[](../../../../samples/Prague.Samples/Concepts/SortedPaging.cs#tied-comparer)]

[!code-csharp[](../../../../samples/Prague.Samples/Concepts/SortedPaging.cs#tied-comparer-plans-agree)]

[!code-csharp[](../../../../samples/Prague.Samples/Concepts/SortedPaging.cs#pages-partition)]

Encounter order is the order the candidate set yields rows, which for one cache and one query is
repeatable but is not something you can predict or build on. If the order within a tie group matters
to a reader, make the comparer **total** — fall back to the primary key when the sort key ties:

[!code-csharp[](../../../../samples/Prague.Samples/Concepts/SortedPaging.cs#total-comparer)]

A total order has exactly one sorted permutation, so the page is then defined by the data alone:

[!code-csharp[](../../../../samples/Prague.Samples/Concepts/SortedPaging.cs#total-comparer-pages)]

Neither plan gives you snapshot isolation: pages partition an *unchanged* result. Writes landing
between two page queries can still move a row across the boundary.

After `Sort` / `SortBounded` only the outer, unfiltered `JoinWith{T}()` binds. On a
[frozen query](prepared-queries.md) the seed lane — and so the encounter order — is chosen per
execution, which leaves the order inside a tie group unspecified there; a total comparer gives
byte-identical pages on the frozen path too, and `FrozenOptions.PreserveEagerOrder` pins the
ordinals back to the eager ones.

## Allocation

`SortBounded` on a page allocates **nothing**. The comparer reaches the selection code as a struct
type parameter, so there is no `Comparison<T>` delegate and no boxed `IComparer<T>`, and the heap
buffer is pooled. `Sort` is 0 B per query with a struct comparer as well: the comparer travels as a
type parameter through every frame and the sort runs over a pooled index array. Passing a reference
comparer — the generated `{Name}Comparer` fields are `IComparer<T>` — trades that for an interface
call per comparison, still with no per-row allocation.

## Measured

Apple M4 Pro, .NET 9, both plans in the same process, BenchmarkDotNet ShortRun. Read as
order-of-magnitude — error bars on a laptop are wide.

| shape | `SortBounded` | `Sort` | |
|---|---|---|---|
| page of 20, 100k rows | **940 µs** | 8,066 µs | 8.6× faster |
| page of 200, 10k rows | **194 µs** | 449 µs | 2.3× faster |
| page of 20 at the end, 100k rows | **2,192 µs** | 7,553 µs | 3.4× faster |
| joined page of 20, 100k rows | **4,847 µs** | 24,404 µs | 5.0× faster |
| half the result, 100k rows | 10,281 µs | **7,766 µs** | 1.3× slower |
| the whole result, 100k rows | 16,241 µs | **7,555 µs** | 2.1× slower |
