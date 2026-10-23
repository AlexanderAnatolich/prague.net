---
title: Page an API
description: Serve a sorted, paged list endpoint from a cache with offset or cursor pages, a comparer that keeps pages disjoint, DTOs that leave the pooled buffer behind, and a frozen query for the hot path.
---

# Page an API

**Goal:** a `GET /customers/{id}/orders?skip=&take=` endpoint that returns a stable page, the total, and nothing that points into a pooled buffer.

**Prerequisite:** an `OrderCache` (see [Quick Start](../getting-started/quick-start.md)). The code is in [`PageAnApi.cs`](https://github.com/nanov/prague.net/blob/main/samples/Prague.Samples/HowTo/PageAnApi.cs); every block runs as a test.

## 1. Pick a comparer that never returns 0 for two different rows

Paging over a sort that has ties is where duplicates and gaps come from: rows with equal keys can land on either side of a page boundary from one call to the next. Add the primary key as the last tie-break.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/PageAnApi.cs#page-total-comparer)]

A `readonly struct` comparer is passed by value, so the sort step is devirtualized. `Order` also declares `[DataCacheSort("ByCreatedAt", "CreatedAtUnixMs:-1", "OrderId:1")]`, which generates the equivalent `OrderCache.ByCreatedAtComparer`; use that when the sort is a fixed property of the entity, write a struct when it is a property of the endpoint.

## 2. Define the DTOs

Rows from `ExecutePooled` are the cache's own instances and the buffer that holds them is on loan. Copy what the response needs before the buffer goes back.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/PageAnApi.cs#page-dto)]

## 3. Offset page with `SortBounded`

`SortBounded(comparer).ExecutePooled(skip, take)` selects the page with a bounded top-k pass; the rows outside the window are never sorted. `TotalCount` is the match count before paging.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/PageAnApi.cs#page-offset)]

When you need the whole result sorted, use `Sort`: it is the better plan when there is no page to bound.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/PageAnApi.cs#page-whole-result)]

The difference between the two, and when each wins, is measured in [Sorted Paging](../core-concepts/sorted-paging.md).

## 4. Cursor page

An offset page shifts when rows are inserted ahead of it. A cursor page encodes the last row's sort key `(CreatedAtUnixMs, OrderId)` and asks for everything strictly after it in sort order. The `Range` lane on `CreatedAtUnixMs` narrows candidates; the `Where` drops the tie rows already served.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/PageAnApi.cs#page-cursor)]

The test walks three cursor pages over 25 rows with five-way ties on the timestamp and gets 25 distinct ids.

## 5. Freeze it for the hot path

The endpoint asks the same shape every time, with only the customer and the window changing. `Prepare<TArgs>()` with a struct argument plans it once; `ExecutePooled(in args, skip, take)` costs no allocation per call.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/PageAnApi.cs#page-prepared)]

`PreparedPageMatchesEager` asserts the frozen page is byte-for-byte the eager page. That holds because the comparer is total; an unsorted frozen query has no row-order guarantee at all (see [Prepared Queries](../core-concepts/prepared-queries.md)).

## Pitfalls

- **A non-total comparer.** `TiesWithoutATieBreakAreUnspecifiedUnderSort` shows the symptom: the set of a page is right, the rows inside a tie group are in encounter order, which is not a contract.
- **Returning `QueryResults<T>` or a `ReadOnlySpan<T>` over it from the handler.** The buffer is disposed when the `using` ends; the serializer reads it after that. Return DTOs.
- **`skip` beyond `TotalCount`.** You get an empty page and the correct `TotalCount`, not an exception.
- **`Sort` + `take` for small pages over large matches.** It sorts everything. Use `SortBounded`.
