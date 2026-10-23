---
title: Quick Start
description: Define a DataCache POCO, create or register the generated cache, write a value and run an indexed query with pooled results.
---

# Quick Start

A working cache in three steps.

## 1. Define a POCO

Annotate any data class with `[DataCache]`. Mark its primary key with `[DataCacheKey]` and declare any indices you want to query by.

```csharp
using Prague.Core;

[DataCache]
public partial class Order
{
    [DataCacheKey]
    public required string OrderId { get; init; }

    [DataCacheIndex(DataCacheIndexType.Many)]
    public required int CustomerId { get; init; }

    [DataCacheIndex(DataCacheIndexType.Range)]
    public required long CreatedAtUnixMs { get; init; }

    public required decimal Total { get; init; }
}
```

The class must be `partial`. The source generator emits `OrderCache : IDataCache<OrderCache, string, Order>` alongside it — index storage, `AddOrUpdate`, a fluent `Query()` builder and a `Prepare()` builder — and completes `Order` with `CacheEquals` / `CacheGetHashCode` (deep structural equality) and `Clone()`. Do not implement any interface by hand; `[DataCacheKey]` is enough.

## 2. Create the cache

Stand-alone, for tests and in-process views:

```csharp
var cache = new OrderCache();
```

Through the registry, which also wires joins and global indices between caches:

```csharp
var registry = new DataCacheRegistryBuilder()
    .Register<OrderCache>()
    .Build();
var cache = registry.GetCache<OrderCache>();
```

From Kafka through DI (see [Kafka integration](../advanced/kafka-integration.md)):

```csharp
services.AddKafkaCaches(o => o.BootstrapServers = "kafka:9092",
    builder => builder.AddCache<OrderCache, string, Order>());

var cache = serviceProvider.GetRequiredService<OrderCache>();
```

## 3. Write and query

```csharp
cache.AddOrUpdate(new Order {                       // timestamp = now; AddOrUpdate(order, timestampMs) to set it
    OrderId = "ORD-001",
    CustomerId = 42,
    CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    Total = 99.99m,
});

using var orders = cache.Query()
    .WithCustomerId(42)
    .WithCreatedAtUnixMs(q => q.Gte(yesterdayUnixMs))
    .ExecutePooled();

foreach (var order in orders)
    Console.WriteLine($"{order.OrderId} {order.Total}");
```

Notes:

- `WithCustomerId` and `WithCreatedAtUnixMs` are generated from the `[DataCacheIndex]` annotations — IntelliSense lists exactly the indices you declared.
- `ExecutePooled()` returns a disposable `QueryResults<Order>` backed by `ArrayPool`. Always `using` it. `Execute()` allocates and needs no disposal.
- Index lanes are intersected; the first one that narrows to nothing stops the query.
- A second `AddOrUpdate` of a structurally equal `Order` is a no-op (`Same`): nothing is swapped and no index is touched. `cache.AddOrUpdate(order, out var previous)` returns `false` in that case.

## Next

- [Index Types](../core-concepts/index-types.md) — the full menu of `[DataCacheIndex]` variants plus `[DataCacheValueIndex]`, `[DataCacheHasValueIndex]`, and friends.
- [Query Engine](../core-concepts/query-engine.md) — pooled vs allocating execution, `UseIndex`, `Or`, `Where`, sort, and intersection rules.
- [Prepared and Frozen Queries](../core-concepts/prepared-queries.md) — build a query once, execute it at 0 B.
- [Joins](../core-concepts/joins.md) — `JoinWith{Other}` and `InnerJoinWith{Other}` for cross-cache lookups.
