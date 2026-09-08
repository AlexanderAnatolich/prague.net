---
title: Sorted Paging
---

# Sorted Paging

Sorted queries come in two execution plans, and you choose one. Nothing is inferred from the
arguments, because the plans order comparer-equal rows differently — so which one runs has to be
your decision, not the engine's.

```csharp
// Paging a large result: select just the page, never sort the rest.
using var page = orderCache.Query()
    .WithCustomerId(42)
    .SortBounded(new ByCreatedAtDesc())
    .ExecutePooled(skip: 40, take: 20);

// Materialising a whole result: classic full sort.
using var all = orderCache.Query()
    .WithCustomerId(42)
    .Sort(new ByCreatedAtDesc())
    .ExecutePooled();
```

## Which one

| You are… | Use | Why |
|---|---|---|
| paging — `take` is a small slice of what matched (tens to a few hundred rows) | `SortBounded` | 2.3–8.6× faster; `skip` is irrelevant, so deep pages win too |
| taking a large chunk — `take` covers roughly a quarter or more of what matched | `Sort` | `SortBounded` is 1.3–2.7× slower there: no sort left to skip, only overhead |
| calling `Execute*()` with no page at all | `Sort` | unbounded always runs the classic sort anyway |
| paging with a comparer that can **tie** | `SortBounded` — required | it breaks ties by encounter order, so consecutive pages partition the result |

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

## Ties decide it, not speed

A comparer that returns `0` for two distinct rows leaves their relative order **unspecified**. Paging
with `Sort` under such a comparer can therefore repeat a row on one page and drop it from the next —
exactly like SQL `ORDER BY … LIMIT/OFFSET` without a unique tiebreaker.

Two ways out:

1. **Use `SortBounded`.** It carries each row's encounter ordinal and breaks ties by it, so
   consecutive pages of an unchanged result partition it with no duplicates and no gaps.
2. **Make the comparer total** — fall back to the primary key when the primary keys differ:

   ```csharp
   public int Compare(Order? x, Order? y) {
       var byDate = y!.CreatedAt.CompareTo(x!.CreatedAt);
       return byDate != 0 ? byDate : x.Id.CompareTo(y.Id);   // total: no ties between distinct rows
   }
   ```

With a total comparer both plans return byte-identical output — a total order has exactly one sorted
permutation — and the choice becomes purely a performance one.

Neither plan gives you snapshot isolation: pages partition an *unchanged* result. Writes landing
between two page queries can still move a row across the boundary.

## Allocation

`SortBounded` on a page allocates **nothing**. The comparer reaches the selection code as a struct
type parameter, so there is no `Comparison<T>` delegate and no boxed `IComparer<T>`, and the heap
buffer is pooled. `Sort` costs roughly 88 B/query on a simple shape and 168 B on a joined one.

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
