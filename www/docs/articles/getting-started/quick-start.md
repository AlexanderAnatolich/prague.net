---
title: Quick Start
description: Define a DataCache POCO, create or register the generated cache, write a value and run an indexed query with pooled results.
---

# Quick Start

A working cache in three steps, and a fourth that reads its indices directly.

## 1. Define a POCO

Annotate any data class with `[DataCache]`. Mark its primary key with `[DataCacheKey]` and declare any indices you want to query by. The `Order` below is the entity every page of these docs shares, so it carries a few annotations a first cache does not need; the comments say what each one is for. Everything lives in `using Prague.Core;`.

[!code-csharp[](../../../../samples/Prague.Samples/Models/Order.cs#order-entity)]

The class must be `partial`. The source generator emits `OrderCache : IDataCache<OrderCache, string, Order>` alongside it — index storage, `AddOrUpdate`, a fluent `Query()` builder and a `Prepare()` builder — and completes `Order` with `CacheEquals` / `CacheGetHashCode` (deep structural equality) and `Clone()`. Do not implement any interface by hand; `[DataCacheKey]` is enough.

A `[DataCacheForeignKey<T>]` also indexes the property it sits on, which is why `Order` gets `WithCustomerId(...)` without a separate `[DataCacheIndex]`; the join it declares is covered in [Joins](../core-concepts/joins.md).

## 2. Create the cache

Stand-alone, for tests and in-process views:

[!code-csharp[](../../../../samples/Prague.Samples/GettingStarted/QuickStart.cs#create-cache-standalone)]

Through the registry, which also wires joins and global indices between caches (registering `OrderCache` pulls in the caches its foreign keys point at):

[!code-csharp[](../../../../samples/Prague.Samples/GettingStarted/QuickStart.cs#create-cache-registry)]

From Kafka through DI (see [Kafka integration](../advanced/kafka-integration.md)); the cache resolves as soon as the container is built and fills once the hosted consumer has replayed the topic:

[!code-csharp[](../../../../samples/Prague.Samples/GettingStarted/QuickStart.cs#create-cache-kafka)]

## 3. Write and query

[!code-csharp[](../../../../samples/Prague.Samples/GettingStarted/QuickStart.cs#write-and-query)]

Notes:

- `WithCustomerId` and `WithCreatedAtUnixMs` are generated from the index annotations (`[DataCacheForeignKey<Customer>]` and `[DataCacheIndex(Range)]`) — IntelliSense lists exactly the indices you declared.
- `ExecutePooled()` returns a disposable `QueryResults<Order>` backed by `ArrayPool`. Always `using` it. `Execute()` allocates and needs no disposal.
- Index lanes are intersected; the first one that narrows to nothing stops the query.
- A second `AddOrUpdate` of a structurally equal `Order` is a no-op (`Same`): nothing is swapped, no index is touched, and the `out` overload reports it:

[!code-csharp[](../../../../samples/Prague.Samples/GettingStarted/QuickStart.cs#same-value-is-a-no-op)]

## 4. Direct index access

The indices are public fields on the cache, so a lookup that needs no query can read them directly. A `Many` or `Range` index hands back keys; `cache.Cache` is the raw `InMemoryDataCache<string, Order>` that dereferences them.

[!code-csharp[](../../../../samples/Prague.Samples/GettingStarted/QuickStart.cs#direct-index-access)]

Every block on this page is compiled and run as a test in `samples/Prague.Samples`; `dotnet test samples/Prague.Samples` reproduces it.

## Next

- [Index Types](../core-concepts/index-types.md) — the full menu of `[DataCacheIndex]` variants plus `[DataCacheValueIndex]`, `[DataCacheHasValueIndex]`, and friends.
- [Query Engine](../core-concepts/query-engine.md) — pooled vs allocating execution, `UseIndex`, `Or`, `Where`, sort, and intersection rules.
- [Prepared and Frozen Queries](../core-concepts/prepared-queries.md) — build a query once, execute it at 0 B.
- [Joins](../core-concepts/joins.md) — `JoinWith{Other}` and `InnerJoinWith{Other}` for cross-cache lookups.
