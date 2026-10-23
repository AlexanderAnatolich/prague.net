---
title: Index Types
description: Unique, Many and Range indices, symmetric variants, predicate key-set indices, sort definitions and indexing external types in Prague.
---

# Index Types

Every query in Prague is planned over typed indices. Each `[DataCacheIndex]` (and its specialized cousins below) becomes a strongly-typed field on the generated cache class plus a corresponding `With{Property}(...)` builder method.

## Primary index

`[DataCacheKey]` marks the PK property. It must be applied exactly once per `[DataCache]` type and drives the `TKey` of the underlying `InMemoryDataCache<TKey, TValue>` (`TKey : notnull, IEquatable<TKey>`). The builder narrows by it with `WithKey(...)` / `With{KeyProperty}(...)`.

```csharp
[DataCacheKey]
public required string OrderId { get; init; }
```

## Secondary indices

`[DataCacheIndex(DataCacheIndexType.X)]` declares a secondary index. Three flavours exist:

| Variant | Generated field | Use case |
| --- | --- | --- |
| `Unique` | `CacheUniqueIndex<TKey, TValue, TIndexKey>` | O(1) equality; one entity per index value. |
| `Many` | `CacheKeyValueListIndex<TKey, TValue, TIndexKey>` | O(1) equality; many entities per index value. |
| `Range` | `CacheRangeIndex<TKey, TValue, TIndexKey>` | Ordered range scans over a pooled B+tree; property must implement `IComparable<T>`. |

`Many` is the default if you omit `IndexType`. A `Many` index takes a per-key bucket capacity hint: `[DataCacheIndex(DataCacheIndexType.Many, 4)]` (other types reject it at compile time).

```csharp
[DataCacheIndex(DataCacheIndexType.Unique)] public required string Email { get; init; }
[DataCacheIndex(DataCacheIndexType.Many)]   public required int CustomerId { get; init; }
[DataCacheIndex(DataCacheIndexType.Range)]  public required long CreatedAtUnixMs { get; init; }
```

`Unique` and `Many` emit `WithProperty(...)` overloads for a single value, a nullable value (`null` skips the lane), a `ReadOnlySpan<T>`, an array and a `List<T>`. A multi-value call is an OR within that lane:

```csharp
query.WithEmail("alice@example.com");
query.WithEmail(new[] { "alice@example.com", "bob@example.com" });   // array / ReadOnlySpan
query.WithEmail(new List<string> { "alice@example.com", "bob@example.com" });
```

The index fields are public too: `cache.EmailIndex.TryGetValue(email, out var key)` (Unique), `cache.CustomerIdIndex.GetValues(42)` (Many), `cache.CreatedAtUnixMsIndex.GetValuesBetween(a, b)` / `GetValuesGte(a)` (Range).

`Range` indices instead expose a sub-builder:

```csharp
query.WithCreatedAtUnixMs(q => q.Gte(yesterdayMs));
query.WithCreatedAtUnixMs(q => q.Gte(startMs).Lt(endMs));
query.WithCreatedAtUnixMs(q => q.Lt(nowMs));
```

The sub-builder is type-state: open with `Gt` / `Gte` or `Lt` / `Lte`, then optionally close the other side. There is no `Between` or `Eq`; a disjunction is the query-level `.Or(...)`.

## Symmetric variants

`Symmetric = true` upgrades a `Many` index to `CacheSymmetricKeyValueListIndex<TKey, TValue, TIndexKey>` and a `Unique` index to `CacheSymmetricUniqueIndex<TKey, TValue, TIndexKey>`. The symmetric variant maintains a reverse map (`TKey → TIndexKey`), required for left-index-driven joins, where the left entity carries the index value rather than the right's primary key. See [Joins](joins.md).

```csharp
[DataCacheIndex(DataCacheIndexType.Many, Symmetric = true)]
public required int CustomerId { get; init; }
```

Foreign keys declared with `[DataCacheForeignKey<T>(ManyToOne)]` get symmetric upgrades automatically.

## Predicate-based key-set indices

When a query is repeatedly filtered by `value OP constant` against a property, a key-set index — the set of keys for which the predicate holds, evaluated once per write — is cheaper than a `Many` index: it stores keys only, and the lane is one set lookup. Prague offers four variants:

| Attribute | Builder method | What it tracks |
| --- | --- | --- |
| `[DataCacheHasValueIndex]` | `WithProperty()` | Keys where `Property != null` (for ref / nullable types) or `Property == true` (for bool). |
| `[DataCacheHasNotValueIndex]` | `WithoutProperty()` | Keys where `Property == null` (for ref / nullable types) or `Property == false` (for bool). |
| `[DataCacheValueIndex(CompareOp, value)]` | `With{IndexName}()` | Keys where `Property OP value` is true. |
| `[DataCacheNoValueIndex(CompareOp, value)]` | `Without{IndexName}()` | Keys where `Property OP value` is false. |

```csharp
[DataCacheHasValueIndex]
public DateTime? SettledAt { get; init; }

[DataCacheValueIndex(CompareOp.Equal, OrderStatus.Pending)]
public required OrderStatus Status { get; init; }
```

All four are backed by `CacheKeySetIndex`; the predicate is evaluated once per write. `CompareOp` is `Equal`, `NotEqual`, `GreaterThan`, `GreaterThanOrEqual`, `LessThan`, `LessThanOrEqual`. An optional `IndexName` names the generated verb (`With{IndexName}()` / `Without{IndexName}()`).

## Sort definitions

`[DataCacheSort("Name", "Prop:1", "OtherProp:-1")]` declares a class-level comparer that you can pass to `Sort(...)` or `SortBounded(...)` on a query. `:1` is ascending, `:-1` is descending. Multiple sort attributes per class are allowed:

```csharp
[DataCache]
[DataCacheSort("ByDate", nameof(CreatedAtUnixMs) + ":-1")]
[DataCacheSort("ByCustomerThenDate", nameof(CustomerId) + ":1", nameof(CreatedAtUnixMs) + ":-1")]
public partial class Order { ... }
```

The generator emits `OrderCache.ByDateComparer` and `OrderCache.ByCustomerThenDateComparer` as `static readonly IComparer<Order>`. Use them with `.Sort(OrderCache.ByDateComparer)` or `.SortBounded(...)` — see [Sorted Paging](sorted-paging.md) for which to choose. Any `IComparer<T>` works; a `readonly struct` comparer makes either sort allocation-free.

## External types

If you can't add attributes to the source POCO (third-party DTOs, generated proto classes), use the type-parameter form `[DataCache<T>]` plus class-level `[DataCacheIndex("PropertyName", DataCacheIndexType.X)]` and the property-name form of the predicate indices:

```csharp
[DataCache<ExternalDto>(nameof(ExternalDto.Id))]
[DataCacheIndex(nameof(ExternalDto.CustomerId), DataCacheIndexType.Many)]
[DataCacheValueIndex(nameof(ExternalDto.Status), CompareOp.Equal, OrderStatus.Pending)]
public partial class ExternalDtoCache;
```

The generated `CacheEquals` / `Clone` machinery for the external type is also emitted on this class.

## Global last-update index

`[DataCacheGlobalLastUpdateIndex<TIndex>]` on a property tracks the latest update per group key, shared across caches, and emits `UpdatedAfter(...)` when the group key is the primary key. See [Global Last Update Index](../advanced/global-last-update-index.md).

## Next

- [Query Engine](query-engine.md) — how the planner intersects these indices.
- [Joins](joins.md) — how indices participate in cross-cache joins.
