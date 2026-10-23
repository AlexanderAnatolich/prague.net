---
title: Map, Clone, Batch
description: Project query results without a second buffer, get a private copy of a row when you need to mutate it, and read many keys in one call.
---

# Map, Clone, Batch

**Goal:** get results into the shape the caller needs with the fewest copies, and never hand out a cache instance that someone will mutate.

**Prerequisite:** an `OrderCache`. The code is in [`MapCloneBatch.cs`](https://github.com/nanov/prague.net/blob/main/samples/Prague.Samples/HowTo/MapCloneBatch.cs); every block runs as a test.

## 1. Project with `Map`

`Map` returns a result of the same pooled kind holding the projected items and disposes the source for you; `TotalCount` is carried over.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/MapCloneBatch.cs#map-results)]

The `using` on the mapped result is the one that matters; disposing the source afterwards is a safe no-op.

## 2. Conversions that also consume

`ToArray`, `ToList`, `ToHashSet` and `ToDictionary` copy out and return the buffer in one step. On a non-pooled `Execute()` whose buffer is exactly full, `ToArray` hands back the buffer itself. The implicit `ReadOnlySpan<T>` conversion is a view, valid until `Dispose`.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/MapCloneBatch.cs#map-conversions)]

## 3. Clone a row

Rows are the cache's own instances. If you need to mutate one (patch a DTO in place, stage an update), take a copy. The generated `Clone()` is deep, collection-aware and reflection-free.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/MapCloneBatch.cs#clone-generated)]

The `*Cloned` terminals clone every row on the way out; `CloneElements()` does it in place on a result you already hold.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/MapCloneBatch.cs#clone-terminals)]

## 4. Read several keys

The cache exposes batch lookups that skip the query builder entirely.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/MapCloneBatch.cs#batch-reads)]

When the batch needs lanes or filters as well, the primary key is a lane like any other, with several values meaning OR within the lane.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/MapCloneBatch.cs#batch-with-key)]

## What allocates

| Call | Allocation |
|---|---|
| `ExecutePooled()` + `using` | none (pooled buffer) |
| `.Map(f)` on pooled | none beyond the projected items |
| `ToList()` / `ToArray()` on pooled | one collection |
| `ExecutePooledCloned()`, `Clone()` | one object graph per row |

## Pitfalls

- **Mutating a row from `ExecutePooled()`.** Every other reader sees it, and the cache's equality check on the next message compares against your mutation. Clone first.
- **Keeping the `ReadOnlySpan<T>` past the `using`.** The buffer is back in the pool and may be rented by another query.
- **Cloning by default.** It costs an allocation per row; most read paths copy into a DTO anyway, which is one allocation and a narrower shape.
