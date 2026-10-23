---
title: Global Last Update Index
description: Track the latest update per group key across several caches and query everything changed since a watermark for incremental sync.
---

# Global Last Update Index

The global last-update index tracks the timestamp of the most recent change for every entity in a cache, grouped by a property you nominate. It powers incremental sync APIs: "give me everything that changed since `t`." Every block on this page runs against the sample domain and is asserted in `samples/Prague.Samples/Advanced/GlobalLastUpdateIndex.cs`.

## Why it's separate

Prague's normal indices are scoped to one cache. The global index sits **above** caches: one `IDataCacheGlobalLastUpdateIndex<TGroupKey>` instance can collect timestamps from many caches whose entities share a `TGroupKey`. In the sample domain `Customer` and `Order` both carry a customer id; one global index keyed by it becomes the single source of truth for "what changed for customer 42 since last sync" — a profile edit and a new order alike.

## Declaring the index

Define a `partial` class that implements `IDataCacheGlobalLastUpdateIndex<TGroupKey>`. The interface is implemented for you by the generator:

[!code-csharp[](../../../../samples/Prague.Samples/Models/CustomerActivityIndex.cs)]

Then mark the group-key property on each contributing cache with `[DataCacheGlobalLastUpdateIndex<TIndex>]`. On `Customer` the group key is the primary key; on `Order` it is the `CustomerId` foreign key:

[!code-csharp[](../../../../samples/Prague.Samples/Models/Customer.cs)]

[!code-csharp[](../../../../samples/Prague.Samples/Models/Order.cs#order-entity)]

Both caches now report into `CustomerActivityIndex` on every write.

A filtered variant, `[DataCacheGlobalLastUpdateIndex<TIndex, TFilter>]`, tracks only the entities a `readonly struct : IDataCacheGlobalLastUpdateFilter<TEntity>` admits (a `static abstract bool Include(TEntity)`); membership is re-evaluated on every update, so an entity that stops qualifying leaves the index.

## Timestamp source

By default the index uses the `timestampMs` argument passed to `AddOrUpdate(value, timestampMs)` (which, for Kafka-backed caches, is the message timestamp; the single-argument `AddOrUpdate(value)` stamps the current time). If you'd rather drive it from an entity property, name it in the attribute — `[DataCacheGlobalLastUpdateIndex<CustomerActivityIndex>(nameof(UpdatedAtUnixMs))]` — and give it type `long` (Unix milliseconds), `DateTime` or `DateTimeOffset`.

## Resolving the index

The index is a singleton: resolve it from DI when the caches come from `AddKafkaCaches`, or from the registry otherwise.

[!code-csharp[](../../../../samples/Prague.Samples/Advanced/GlobalLastUpdateIndex.cs#resolve-index)]

Its public surface is `IDataCacheGlobalLastUpdateIndex<TGroupKey>`:

[!code-csharp[](../../../../samples/Prague.Samples/Advanced/GlobalLastUpdateIndex.cs#index-surface)]

`TryGetMin` / `TryGetMax` come with and without the `out TGroupKey key` parameter and return `false` on an empty index. `GetEntitiesCount(key)` counts the entities tracked under a group across every contributing cache. `Index` is the underlying `LastUpdatedIndex<TGroupKey>` with `GetValuesGt` / `GetValuesGte` / `GetValuesLt` / `GetValuesBetween` in `long`, `DateTime` and `DateTimeOffset` forms; they return the group keys that changed in the window.

## Querying

**When the group key is the cache's primary key** (`Customer` above), the generator emits `UpdatedAfter(...)` on the query builder — the same thing as `UseIndex(globalIndex, …)` on the raw cache:

[!code-csharp[](../../../../samples/Prague.Samples/Advanced/GlobalLastUpdateIndex.cs#updated-after)]

The customer 42 row comes back although nothing was written to the customer itself after the sync point — its order was, and the order's write stamps the group. The `out long max` form reports the highest timestamp among the rows returned, which is the watermark to commit for the next call:

[!code-csharp[](../../../../samples/Prague.Samples/Advanced/GlobalLastUpdateIndex.cs#updated-after-watermark)]

On the eager builder `UpdatedAfter` takes a `long` or a `DateTimeOffset`, each with the `out long max` twin. The prepared builder adds the bound `(after, untilInclusive)` window form in `long`, `DateTime` and `DateTimeOffset`, and the argument forms `UpdatedAfter(static a => a.Since)` and `UpdatedAfter(static a => a.Since, static a => a.Until)`. It binds only when the global index is keyed by the cache's primary key type.

**When the group key is not the primary key** (`Order` above), read the index itself: which groups changed, then query each group's rows through its own index.

[!code-csharp[](../../../../samples/Prague.Samples/Advanced/GlobalLastUpdateIndex.cs#group-key-not-pk)]

## Watermark semantics

- `UpdatedAfter(t)` returns entities with timestamp **strictly greater than `t`**.
- There is no snapshot isolation: a write landing during the query may or may not be included. The `out long max` form reports the highest timestamp among the rows actually returned, which is the safe watermark to commit.
- The index is not transactional across caches: if you query several caches separately, the watermarks you observe may differ. Take the **minimum** of the per-cache watermarks as your sync point.

## When to use it

The global index has a per-write cost (a sorted-index update per contributing cache). It pays off when:

- You serve incremental APIs (`/api/changes?since=...`).
- You need to forward changes to a downstream system on a polling cadence.
- You feed an external search index that wants delta updates.

For "tail the topic in real time" use-cases, `ICacheAfterHandler` is the cheaper hook.

## Next

- [Performance Tuning](performance-tuning.md).
