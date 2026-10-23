---
title: Generator Diagnostics
description: Every CACHExxx and DATACACHExxx diagnostic the Prague source generator and analyzer report, when each fires, a minimal example and the fix, plus the ordinary compiler errors a mis-annotated model produces.
---

# Generator diagnostics

The `Prague.Codegen` analyzer validates the model before it emits a cache. Everything it rejects is reported as a `CACHExxx` diagnostic in the `CacheGenerator` category (CACHE005–007 use `DataCache`); all are errors except CACHE042, a warning. The `[DataCacheFrom<T>]` code fix reports two `DATACACHExxx` diagnostics of its own. Ids 003, 024–029 and 036–039 are unused.

Two things to know when reading a build log:

- A diagnostic stops the cache from being emitted, so it usually arrives with follow-up compiler errors (`CS0246: The type or namespace name 'OrderCache' could not be found`, CS0535 on an index class whose partial half is missing, …). Fix the `CACHE` line first; the rest disappears with it.
- The location is the annotated property, or the class when the attribute sits on the class. Rider and Visual Studio show them live; `dotnet build` prints them with the rest.

The examples below are deliberately broken and are not part of the compiled samples. The attributes themselves are described in [Attributes Reference](attributes.md).

## CACHE001 — Multiple DataCacheKey attributes

Fires when more than one property of a `[DataCache]` type carries `[DataCacheKey]`.

```csharp
[DataCache]
public partial class Order {
  [DataCacheKey] public string Id { get; init; }
  [DataCacheKey] public int CustomerId { get; init; }   // CACHE001
}
```

Keep one key. If the second property should be queryable, give it `[DataCacheIndex]`; if the real key is composite, build a record struct and use that.

## CACHE002 — Invalid Range index type

Fires when `[DataCacheIndex(DataCacheIndexType.Range)]` sits on a property whose type implements neither `IComparable<T>` nor is a built-in comparable (numbers, `string`, `DateTime`, `DateTimeOffset`, `Guid`, enums, …). Comes with CS0311 from the generated `CacheRangeIndex<…>` field.

```csharp
public sealed class Money { public decimal Amount; }

[DataCache]
public partial class Order {
  [DataCacheKey] public string Id { get; init; }
  [DataCacheIndex(DataCacheIndexType.Range)] public Money Total { get; init; }   // CACHE002
}
```

Index the comparable component (`decimal Total`) or implement `IComparable<Money>`.

## CACHE004 — Nested cache class not supported

Fires when a `[DataCache]` type is declared inside another type.

```csharp
public static class Models {
  [DataCache]
  public partial class Order {   // CACHE004
    [DataCacheKey] public string Id { get; init; }
  }
}
```

Move the type to namespace level. Generated partials and extension classes are emitted beside it and cannot reach into a container.

## CACHE005 — Cannot clone Dictionary with object or dynamic value type

Fires for a `Dictionary<TKey, object>` / `Dictionary<TKey, dynamic>` property. `Clone()` deep-copies collections and has no safe way to copy an untyped value.

```csharp
[DataCache]
public partial class Order {
  [DataCacheKey] public string Id { get; init; }
  public Dictionary<string, object> Extras { get; init; }   // CACHE005
}
```

Use a concrete value type, a `JsonElement`, or a serialized `string`.

## CACHE006 — Cannot clone collection with object or dynamic element type

Same rule for `List<object>`, `object[]`, `IReadOnlyList<dynamic>` and the other sequence types the cloner handles.

```csharp
public List<object> Tags { get; init; }   // CACHE006
```

## CACHE007 — Cannot clone HashSet with object or dynamic element type

Same rule for `HashSet<object>` / `HashSet<dynamic>`.

```csharp
public HashSet<dynamic> Flags { get; init; }   // CACHE007
```

## CACHE008 — Invalid foreign key reference

Fires when the `T` in `[DataCacheForeignKey<T>]` (or the selector form) is not a `[DataCache]` type.

```csharp
public sealed class Customer { public int Id { get; init; } }   // no [DataCache]

[DataCache]
public partial class Order {
  [DataCacheKey] public string Id { get; init; }
  [DataCacheForeignKey<Customer>(DataCacheJoinType.ManyToOne)] public int CustomerId { get; init; }   // CACHE008
}
```

Annotate `Customer` or point the foreign key at a type that is cached.

## CACHE009 — Foreign key type mismatch

Fires when the FK property's type is not the referenced cache's key type.

```csharp
[DataCache]
public partial class Customer {
  [DataCacheKey] public int Id { get; init; }
}

[DataCache]
public partial class Order {
  [DataCacheKey] public string Id { get; init; }
  [DataCacheForeignKey<Customer>(DataCacheJoinType.ManyToOne)] public string CustomerId { get; init; }   // CACHE009: string vs int
}
```

Change the property type, or — when the value needs converting — use `[DataCacheForeignKey<Customer, TSelector>]` with a selector that maps `string` to `int`.

## CACHE010 — Nested external cache class not supported

The `[DataCache<T>]` counterpart of CACHE004: the cache class is nested.

```csharp
public static class Caches {
  [DataCache<LegacyProductRecord>(nameof(LegacyProductRecord.Code))]
  public partial class LegacyProductCache;   // CACHE010
}
```

## CACHE011 — External cache class must be partial

Fires when the `[DataCache<T>]` class is not `partial`. (The plain `[DataCache]` form has no equivalent diagnostic; it fails with CS0260, see below.)

```csharp
[DataCache<LegacyProductRecord>(nameof(LegacyProductRecord.Code))]
public class LegacyProductCache;   // CACHE011
```

## CACHE012 — Key property not found

Fires when `KeyProperty` in `[DataCache<T>]` names something that is not a public readable property of `T`.

```csharp
[DataCache<LegacyProductRecord>("Sku")]   // CACHE012: LegacyProductRecord has Code, not Sku
public partial class LegacyProductCache;
```

Use `nameof(LegacyProductRecord.Code)` so renames follow.

## CACHE013 — Key property required

Fires when `[DataCache<T>]` has no `KeyProperty`, `T` has no `[DataCacheKey]` and does not implement `IDataCacheItem<TKey, TValue>`.

```csharp
[DataCache<LegacyProductRecord>]   // CACHE013
public partial class LegacyProductCache;
```

Pass the key property name, or annotate the item type.

## CACHE014 — Index property name required

Fires when a class-level `[DataCacheIndex]` on a `[DataCache<T>]` cache does not name a property.

```csharp
[DataCache<LegacyProductRecord>(nameof(LegacyProductRecord.Code))]
[DataCacheIndex(DataCacheIndexType.Many)]   // CACHE014
public partial class LegacyProductCache;
```

Use `[DataCacheIndex(nameof(LegacyProductRecord.Category), DataCacheIndexType.Many)]`.

## CACHE015 — Index property not found

Fires when the property named in a class-level `[DataCacheIndex]` does not exist on `T`.

```csharp
[DataCacheIndex("Categories", DataCacheIndexType.Many)]   // CACHE015
```

## CACHE016 — Invalid Range index type

CACHE002 for the class-level form: the named property is not comparable.

```csharp
[DataCacheIndex(nameof(LegacyProductRecord.Dimensions), DataCacheIndexType.Range)]   // CACHE016 when Dimensions is a plain class
```

## CACHE017 — Property name required for DataCacheIgnoreEquality

Fires when a class-level `[DataCacheIgnoreEquality]` on a `[DataCache<T>]` cache has no property name.

```csharp
[DataCache<LegacyProductRecord>(nameof(LegacyProductRecord.Code))]
[DataCacheIgnoreEquality]   // CACHE017
public partial class LegacyProductCache;
```

## CACHE018 — Property not found for DataCacheIgnoreEquality

Fires when the property named in a class-level `[DataCacheIgnoreEquality]` does not exist on `T`.

```csharp
[DataCacheIgnoreEquality("LastSynced")]   // CACHE018
```

## CACHE019 — Key property conflict

Fires when `[DataCache<T>]` specifies `KeyProperty` but `T` also has a `[DataCacheKey]`, on a different property.

```csharp
public partial class Record {
  [DataCacheKey] public string Code { get; init; }
  public int Number { get; init; }
}

[DataCache<Record>(nameof(Record.Number))]   // CACHE019
public partial class RecordCache;
```

Drop the `KeyProperty` and let the item type's key win.

## CACHE020 — Topic conflict

Fires when both the `[DataCache<T>]` cache class and the item type `T` carry `[DataCacheTopic]` with different templates.

```csharp
[DataCacheTopic("products.v1")]
public partial class Record { [DataCacheKey] public string Code { get; init; } }

[DataCache<Record>]
[DataCacheTopic("products.v2")]   // CACHE020
public partial class RecordCache;
```

Keep one of them — the item type's wins when both are equal.

## CACHE021 — Key property cannot be indexed as Many

Fires when the `[DataCacheKey]` property also carries `[DataCacheIndex]` with the default or explicit `Many` type.

```csharp
[DataCacheKey]
[DataCacheIndex]   // CACHE021: default is Many
public string Id { get; init; }
```

Use `Unique` or `Range` on a key, or remove the index — `WithKey(…)` / `With{Key}(…)` already exist.

## CACHE022 — Invalid HasValue index type

Fires when `[DataCacheHasValueIndex]` sits on a non-nullable value type other than `bool`.

```csharp
[DataCacheHasValueIndex] public int Attempts { get; init; }   // CACHE022
```

Make the property nullable (`int?`) if "has a value" is meaningful, or use `[DataCacheValueIndex(CompareOp.NotEqual, 0)]`.

The same id is also reported, with the CACHE021 message, when a class-level `[DataCacheIndex(nameof(Key), Many)]` on a `[DataCache<T>]` cache targets the key property. Read the message, not the id.

## CACHE023 — Invalid HasNotValue index type

The `[DataCacheHasNotValueIndex]` twin of CACHE022.

```csharp
[DataCacheHasNotValueIndex] public DateTime ShippedAt { get; init; }   // CACHE023: use DateTime?
```

## CACHE030 — Invalid global last update index type

Fires when `TIndex` in `[DataCacheGlobalLastUpdateIndex<TIndex>]` does not implement `IDataCacheGlobalLastUpdateIndex<TGroupKey>`. The generated half of the index class is withheld, so a class that does declare the interface also gets CS0535 for its members.

```csharp
public partial class OrdersActivity;   // no interface

[DataCache]
public partial class Order {
  [DataCacheKey]
  [DataCacheGlobalLastUpdateIndex<OrdersActivity>]   // CACHE030
  public string Id { get; init; }
}
```

Declare `public partial class OrdersActivity : IDataCacheGlobalLastUpdateIndex<string>`.

## CACHE031 — Global last update index type mismatch

Fires when the annotated property's type differs from the index's `TGroupKey`.

```csharp
public partial class OrdersActivity : IDataCacheGlobalLastUpdateIndex<int>;

[DataCacheKey]
[DataCacheGlobalLastUpdateIndex<OrdersActivity>]   // CACHE031: string vs int
public string Id { get; init; }
```

Align the two; several caches can feed one index only if their group-key properties share a type.

## CACHE032 — Filtered global index: TFilter must implement IDataCacheGlobalLastUpdateFilter&lt;TValue&gt;

Fires when `TFilter` in `[DataCacheGlobalLastUpdateIndex<TIndex, TFilter>]` does not implement the filter interface.

```csharp
public readonly struct OpenOnly;   // no interface

[DataCacheGlobalLastUpdateIndex<OrdersActivity, OpenOnly>]   // CACHE032
public string Id { get; init; }
```

## CACHE033 — Filtered global index: TValue doesn't match the entity type

Fires when the filter implements `IDataCacheGlobalLastUpdateFilter<T>` for a different entity than the one it is applied to.

```csharp
public readonly struct OpenOnly : IDataCacheGlobalLastUpdateFilter<Shipment> {
  public bool Include(Shipment s) => s.Status == "open";
}

[DataCache]
public partial class Order {
  [DataCacheKey]
  [DataCacheGlobalLastUpdateIndex<OrdersActivity, OpenOnly>]   // CACHE033: filter is for Shipment
  public string Id { get; init; }
}
```

## CACHE034 — Filtered global index: TFilter must be accessible to generated code

Fires when the filter struct is `private`, nested in a non-accessible type, or `file`-local. Generated code lives in the same assembly, so `internal` is enough.

```csharp
file readonly struct OpenOnly : IDataCacheGlobalLastUpdateFilter<Order> { … }   // CACHE034 when used
```

## CACHE035 — Global last update index class must be public and top-level

Fires when `TIndex` is not `public` or is nested. The index is registered as a DI singleton and referenced from every cache that feeds it.

```csharp
internal partial class OrdersActivity : IDataCacheGlobalLastUpdateIndex<string>;   // CACHE035 when used
```

## CACHE040 — ManyToOne foreign key on primary key

Fires when `[DataCacheForeignKey<T>(ManyToOne)]` sits on the `[DataCacheKey]` property. Many rows cannot share a key, so the cardinality is a contradiction.

```csharp
[DataCacheKey]
[DataCacheForeignKey<Customer>(DataCacheJoinType.ManyToOne)]   // CACHE040
public int CustomerId { get; init; }
```

Use `OneToOne` for a PK-to-PK relation, or move the FK to its own property.

## CACHE041 — ManyToOne FK conflicts with explicit index type

Fires when a `ManyToOne` foreign key property also has an explicit `[DataCacheIndex(Unique)]` or `[DataCacheIndex(Range)]`. The join needs a symmetric `Many` index.

```csharp
[DataCacheIndex(DataCacheIndexType.Range)]
[DataCacheForeignKey<Customer>(DataCacheJoinType.ManyToOne)]   // CACHE041
public int CustomerId { get; init; }
```

Remove the index (the FK creates one) or make it `Many`. A `Range` index on an FK is not supported; keep a separate property if you need range queries on the same value.

## CACHE042 — ManyToOne FK auto-upgrading explicit index to Symmetric=true (warning)

Fires when a `ManyToOne` foreign key property carries an explicit `[DataCacheIndex(Many)]` without `Symmetric = true`. The generator upgrades it so `JoinWith{T}` can use the `Reverse` map and tells you. With `TreatWarningsAsErrors` this fails the build.

```csharp
[DataCacheIndex(DataCacheIndexType.Many, 8)]
[DataCacheForeignKey<Customer>(DataCacheJoinType.ManyToOne)]   // CACHE042
public int CustomerId { get; init; }
```

Add `Symmetric = true` to the index, or drop it when you only wanted the default.

## CACHE043 — Dual-form FK references unknown property

Fires when the `rightProperty` of `[DataCacheForeignKey<T>(joinType, rightProperty)]` does not exist on `T`.

```csharp
[DataCacheKey]
[DataCacheForeignKey<StockLevel>(DataCacheJoinType.OneToMany, "Warehouse")]   // CACHE043: StockLevel has WarehouseId
public int Id { get; init; }
```

Use `nameof(StockLevel.WarehouseId)`.

## CACHE044 — Dual-form FK type mismatch

Fires when the anchor property and `T.{rightProperty}` have different types.

```csharp
[DataCacheKey]
[DataCacheForeignKey<StockLevel>(DataCacheJoinType.OneToMany, nameof(StockLevel.WarehouseCode))]   // CACHE044: int vs string
public int Id { get; init; }
```

## CACHE045 — Selector FK: TSelector must implement IForeignKeySelector&lt;TFk, TPk&gt;

Fires when the selector type in `[DataCacheForeignKey<T, TSelector>]` does not implement `IForeignKeySelector<TFk, TPk>`.

```csharp
public readonly struct OrderRefSelector;   // no interface

[DataCacheForeignKey<Order, OrderRefSelector>(DataCacheJoinType.ManyToOne)]   // CACHE045
public string OrderRef { get; init; }
```

## CACHE046 — Selector FK: TFk / TPk mismatch

One id, two messages. "TFk doesn't match FK property type" fires when the selector's input type is not the property type; "TPk doesn't match target PK type" when its output type is not `T`'s key type.

```csharp
public readonly struct OrderRefSelector : IForeignKeySelector<int, string> {
  public static string Select(int fk) => fk.ToString();
}

[DataCacheForeignKey<Order, OrderRefSelector>(DataCacheJoinType.ManyToOne)]   // CACHE046: TFk is int, property is string
public string OrderRef { get; init; }
```

## CACHE047 — Selector FK only supports OneToOne / ManyToOne

Fires when a selector-form foreign key declares `OneToMany`. Selectors cannot be inverted, so only the forward direction exists.

```csharp
[DataCacheForeignKey<Order, OrderRefSelector>(DataCacheJoinType.OneToMany)]   // CACHE047
public string OrderRef { get; init; }
```

## CACHE048 — Selector FK: TSelector must be accessible to generated code

Fires when the selector struct is `private`, nested in an inaccessible type, or `file`-local. `internal` is enough.

```csharp
file readonly struct OrderRefSelector : IForeignKeySelector<string, string> { … }   // CACHE048 when used
```

## CACHE049 — Collection foreign key element type mismatch

Fires when a collection FK property (`List<TKey>`, `TKey[]`, …) has an element type that is not the referenced cache's key type.

```csharp
[DataCacheForeignKey<Product>(DataCacheJoinType.ManyToOne)]   // CACHE049: Product key is int
public List<string> ProductIds { get; init; }
```

## CACHE050 — OneToOne foreign key on collection property

Fires when a collection FK property declares `OneToOne`. A collection of keys is by nature many-to-many and is declared `ManyToOne`.

```csharp
[DataCacheForeignKey<Product>(DataCacheJoinType.OneToOne)]   // CACHE050
public List<int> ProductIds { get; init; }
```

## CACHE051 — InitialCapacity is only applicable to Many indexes

Fires when `[DataCacheIndex]` sets `InitialCapacity` (constructor or named argument) on a `Unique` or `Range` index, property-level or class-level. The hint sizes the per-value buckets of a `Many` index and means nothing elsewhere.

```csharp
[DataCacheIndex(DataCacheIndexType.Unique, 16)]   // CACHE051
public string Sku { get; init; }
```

## DATACACHE001 — Generate properties from source type (info)

Reported by the `[DataCacheFrom<TSource>]` analyzer when the attribute has no `sourceHash`, i.e. the properties have not been generated yet. The code fix appends them and records the hash. Not a build error; it appears in the IDE's light-bulb menu.

```csharp
[DataCacheFrom<SupplierDto>]   // DATACACHE001: apply the fix
[DataCache]
public partial class Supplier;
```

## DATACACHE002 — Source type has changed (warning)

Reported when the recorded hash no longer matches the source type's public properties. The code fix regenerates the missing ones; it never deletes properties you have edited. Under `TreatWarningsAsErrors` it fails the build until you re-run the fix or update the hash.

## Common build errors that are not diagnostics

These are plain C# errors a mis-annotated model produces, each reproduced against the current generator. None of them has a `CACHE` line, so there is no hint in the log; this table is the hint.

| Error | Cause | Fix |
| --- | --- | --- |
| `CS0260: Missing partial modifier on declaration of type 'Order'` | A `[DataCache]` type (or the item type of a `[DataCache<T>]`) is not `partial` | Add `partial` |
| `CS0246: The type or namespace name 'OrderCache' could not be found` | `Prague.Codegen` is not referenced as an analyzer, or an earlier `CACHE` error stopped emission | Reference the `Prague` package (it brings the generator), or fix the diagnostic above it |
| `CS1061: 'Order' does not contain a definition for 'GetKey'` reported in a `*.g.cs` file | `[DataCache]` type with no `[DataCacheKey]`: the generator assumes a `string` key and calls `GetKey()` | Add `[DataCacheKey]`, or implement `IDataCacheItem<TKey, Order>` |
| `CS0311: The type 'byte[]' cannot be used as type parameter 'TKey'` | The key (or a `Unique` index) type does not implement `IEquatable<T>` | Use `string`, a number, `Guid`, or a record struct |
| `CS0579: Duplicate 'DataCacheForeignKey<…>' attribute` | Two `[DataCacheForeignKey<T>]` with the same `T` on one property | One FK per target per property |
| `CS0535: 'OrdersActivity' does not implement interface member …` on the index class | Comes with CACHE030/031/035: the generated half of the index class is withheld | Fix the `CACHE` error |
| `CS0534: 'SamplesJsonContext' does not implement inherited abstract member` | A `[DataCacheJsonSerializables]` context with no `[JsonSerializable]` attributes; the Prague generator emits nothing for it | Add `[JsonSerializable(typeof(…))]` like any `JsonSerializerContext` |
| `CS1061: 'Order' does not contain a definition for 'Nope'` in a generated comparer | `[DataCacheSort("ByNope", "Nope:1")]` names a property that does not exist | Fix the name; the generator does not validate sort specs |
| `CS0117: 'OrderCache' does not contain a definition for 'ByIdComparer'` | `[DataCacheSort("ById", "Id")]` — an entry without `:1` / `:-1` is skipped, and a sort with no valid entries emits no comparer | Write `"Id:1"` |
