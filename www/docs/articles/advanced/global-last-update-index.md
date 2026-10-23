---
title: Global Last Update Index
description: Track the latest update per group key across several caches and query everything changed since a watermark for incremental sync.
---

# Global Last Update Index

The global last-update index tracks the timestamp of the most recent change for every entity in a cache, grouped by a property you nominate. It powers incremental sync APIs: "give me everything that changed since `t`."

## Why it's separate

Prague's normal indices are scoped to one cache. The global index sits **above** caches: one `IDataCacheGlobalLastUpdateIndex<TGroupKey>` instance can collect timestamps from many caches whose entities share a `TGroupKey`. A typical use case is a catalog-feed pipeline where `Listing`, `Offer`, and `OrderLine` caches all hold a `ListingId`; one global index keyed by `ListingId` becomes the single source of truth for "what changed in listing 42 since last sync."

## Declaring the index

Define a `partial` class that implements `IDataCacheGlobalLastUpdateIndex<TGroupKey>`. The interface is implemented for you by the generator:

```csharp
public partial class ListingActivityIndex : IDataCacheGlobalLastUpdateIndex<long>;
```

Then mark the group-key property on each contributing cache with `[DataCacheGlobalLastUpdateIndex<TIndex>]`:

```csharp
[DataCache]
public partial class Listing
{
    [DataCacheKey]
    [DataCacheGlobalLastUpdateIndex<ListingActivityIndex>]     // group key == primary key
    public required long ListingId { get; init; }
}

[DataCache]
public partial class Offer
{
    [DataCacheKey] public required string OfferId { get; init; }

    [DataCacheGlobalLastUpdateIndex<ListingActivityIndex>]     // group key is a foreign key here
    public required long ListingId { get; init; }
}
```

Both caches now report into `ListingActivityIndex` on every write.

A filtered variant, `[DataCacheGlobalLastUpdateIndex<TIndex, TFilter>]`, tracks only the entities a `readonly struct : IDataCacheGlobalLastUpdateFilter<TEntity>` admits (a `static abstract bool Include(TEntity)`); membership is re-evaluated on every update.

## Timestamp source

By default the index uses the `timestampMs` argument passed to `AddOrUpdate(value, timestampMs)` (which, for Kafka-backed caches, is the message timestamp). If you'd rather drive it from an entity property:

```csharp
[DataCacheGlobalLastUpdateIndex<ListingActivityIndex>(nameof(UpdatedAtUnixMs))]
public required long ListingId { get; init; }

public required long UpdatedAtUnixMs { get; init; }
```

The property type must be `long` (Unix milliseconds), `DateTime` or `DateTimeOffset`.

## Resolving the index

The index is a singleton: resolve it from DI when the caches come from `AddKafkaCaches`, or from the registry otherwise.

```csharp
var index = serviceProvider.GetRequiredService<ListingActivityIndex>();
var index = registry.GetGlobalIndex<ListingActivityIndex>();
```

Its public surface:

```csharp
public interface IDataCacheGlobalLastUpdateIndex<TGroupKey>
{
    LastUpdatedIndex<TGroupKey> Index { get; }
    bool TryGetMin(out long timestampMs, out TGroupKey key);
    bool TryGetMax(out long timestampMs, out TGroupKey key);
    bool TryGetMin(out long timestampMs);
    bool TryGetMax(out long timestampMs);
    int GetEntitiesCount(TGroupKey key);
}
```

`Index` is the underlying `LastUpdatedIndex<TGroupKey>` with `GetValuesGt` / `GetValuesGte` / `GetValuesBetween` (…) in `long`, `DateTime` and `DateTimeOffset` forms; they return the group keys that changed in the window.

## Querying

**When the group key is the cache's primary key** (the `Listing` cache above), the generator emits `UpdatedAfter(...)` on the query builder — the same thing as `UseIndex(globalIndex, …)` on the raw cache:

```csharp
using var changedListings = listingCache.Query()
    .UpdatedAfter(lastSyncUnixMs)                              // strictly greater than lastSyncUnixMs
    .ExecutePooled();

using var window = listingCache.Query()
    .UpdatedAfter(lastSyncUnixMs, out var maxObservedUnixMs)   // highest timestamp in the result
    .ExecutePooled();

await CommitSyncPoint(maxObservedUnixMs);
```

`UpdatedAfter` has `long`, `DateTime` and `DateTimeOffset` overloads, an `(after, untilInclusive)` window form, the `out long max` form shown above, and prepared twins (`UpdatedAfter(static a => a.Since)`). It binds only when the global index is keyed by the cache's primary key type.

**When the group key is not the primary key** (the `Offer` cache above), read the index itself: which groups changed, then query each group's rows through its own index.

```csharp
foreach (var listingId in index.Index.GetValuesGt(lastSyncUnixMs))
{
    using var offers = offerCache.Query().WithListingId(listingId).ExecutePooled();
    // ...
}
```

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
