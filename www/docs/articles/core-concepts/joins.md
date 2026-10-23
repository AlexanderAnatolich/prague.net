---
title: Joins
description: Declare foreign keys with DataCacheForeignKey and query across Prague caches with generated JoinWith and InnerJoinWith methods, filters and chained joins.
---

# Joins

Prague supports compile-time-safe joins across `[DataCache]` types. The generator wires up the call surface based on `[DataCacheForeignKey<T>]` declarations; the runtime executes the join through one of four resolver families chosen by the relationship shape.

## Declaring a relationship

Place `[DataCacheForeignKey<T>(JoinType)]` on the property that holds the foreign key:

```csharp
[DataCache]
public partial class Author
{
    [DataCacheKey] public required int Id { get; init; }
    public required string Name { get; init; }
}

[DataCache]
public partial class Book
{
    [DataCacheKey] public required int Id { get; init; }

    [DataCacheForeignKey<Author>(DataCacheJoinType.ManyToOne)]
    public required int AuthorId { get; init; }

    public required string Title { get; init; }
}
```

The generator emits:

- A symmetric many-index on `Book.AuthorId` (`BookCache.AuthorIdIndex`), so `WithAuthorId(...)` works too.
- A forward `JoinWithAuthor()` method on `Book`'s query builder; the slot is `Author?`.
- A reverse `JoinWithBook()` method on `Author`'s query builder; the slot is `QueryResults<Book>`.
- `InnerJoinWith{Other}` variants of both that drop lefts with no match.

Which cache gets which method depends on the declaration:

| Declaration | Emitted on | Result slot |
| --- | --- | --- |
| `OneToOne` on a scalar FK | both caches | `TRight?` |
| `OneToMany` on a scalar FK | the target cache only | `QueryResults<TRight>` |
| `ManyToOne` on a scalar FK | both caches | forward `TRight?`, reverse `QueryResults<TRight>` |
| `ManyToOne` on a `List<TKey>` | both caches | `QueryResults<TRight>` |

`DataCacheJoinType` values:

| Value | Meaning |
| --- | --- |
| `OneToOne` | Unique FK — one-to-one in both directions. |
| `ManyToOne` | Canonical: this entity holds the FK; many of us point at one of them. |
| `OneToMany` | Legacy alias; declared from the FK target's perspective. Prefer `ManyToOne`. |

### Dual form

If you cannot place the attribute on the FK property (e.g. you control `Author` but not `Book`), place it on the target's primary key with `nameof(...)`:

```csharp
[DataCacheForeignKey<Book>(DataCacheJoinType.OneToMany, nameof(Book.AuthorId))]
public required int Id { get; init; }
```

The generator treats this exactly like the direct form on `Book.AuthorId` with the inverted cardinality.

### Selector form

When the FK property type doesn't match the target's PK type (compound keys, tuple PKs), use `[DataCacheForeignKey<T, TSelector>]` and supply a `readonly struct` that implements `IForeignKeySelector<TFk, TPk>`:

```csharp
public readonly struct OrderItemOrderKey : IForeignKeySelector<(string, int), string>
{
    public static string Select((string OrderId, int LineNo) fk) => fk.OrderId;
}

[DataCache]
public partial class OrderLine
{
    [DataCacheKey]
    [DataCacheForeignKey<Order, OrderItemOrderKey>(DataCacheJoinType.OneToOne)]
    public required (string OrderId, int LineNo) Key { get; init; }
}
```

Selector-form FKs only support `OneToOne` in the current generator.

## Calling joins

Use the generated methods directly:

```csharp
using var rows = bookCache.Query()
    .WithAuthorId(authorId)
    .JoinWithAuthor()                          // outer; rows kept even if no author
    .ExecutePooled();

foreach (var row in rows)
    Console.WriteLine($"{row.Left.Title} by {row.Right?.Name ?? "unknown"}");
```

`InnerJoinWithAuthor()` drops books whose author is missing:

```csharp
using var rows = bookCache.Query()
    .InnerJoinWithAuthor()
    .ExecutePooled();
```

Caches created through `DataCacheRegistryBuilder` or `AddKafkaCaches` are joined automatically; stand-alone `new XxxCache()` instances need `leftCache.AddJoinedCaches(rightCache, …)` once before joining.

Chained joins are supported up to 5 per query:

```csharp
using var rows = orderCache.Query()
    .WithCustomerId(42)
    .JoinWithCustomer()
    .JoinWithRegion()
    .ExecutePooled();
```

Each `JoinWith{X}` appends a typed slot; the result element type is `JoinResult<Order, Customer?, Region?>`. The fields are `Left`, `Right` (the first joined slot — there is no `Right1`), `Right2`, `Right3`, … Pooled join results rent one shared buffer; a single `Dispose()` on the outer `QueryResults` returns everything.

## Filtering the right side

Every `JoinWith{X}` accepts an optional filter lambda that builds a query against the right cache:

```csharp
.JoinWithAuthor(q => q.WithStatus(AuthorStatus.Active))
```

For zero-allocation static lambdas, pass an argument:

```csharp
.JoinWithAuthor(static (q, status) => q.WithStatus(status), AuthorStatus.Active)
```

The filter callback receives a non-executable builder; only narrowing methods (`WithXxx`, `UseIndex`, `.Or`) are visible — `Where` and `Execute*` are hidden by the discriminator. The callback runs once per query, not per row.

### Or inside the filter

The same `.Or(b1, b2)` clause from the [Query Engine](query-engine.md#or--disjunction) works inside a join filter:

```csharp
// Attach Author only if Country=US OR Genre=Mystery
.JoinWithAuthor(q => q.Or(
    b => b.WithCountry("US"),
    b => b.WithGenre("Mystery")))

// AND-within-branch + OR-across-branches:
//   (Country=US AND Genre=Romance) OR (Country=GB AND Genre=Mystery)
.JoinWithAuthor(q => q.Or(
    b => b.WithCountry("US").WithGenre("Romance"),
    b => b.WithCountry("GB").WithGenre("Mystery")))

// Nested Or — 3-way union:
.JoinWithAuthor(q => q.Or(
    b => b.WithCountry("US"),
    b => b.Or(
        c => c.WithGenre("Mystery"),
        c => c.WithGenre("Romance"))))
```

Internally the join's right-side narrowing runs on a paired-core builder, so the filter operates on a bitmap over the join's paired candidate set — no per-branch candidate-set allocations, SIMD-vectorized cross-branch union, mark-then-prune within branches.

## Sorting after joins

`Sort` / `SortBounded` can be applied either before or after a join. After a sorter, only the outer, unfiltered `JoinWith{T}()` binds — `InnerJoinWith{T}` and the filter overloads must come before it:

```csharp
using var top = bookCache.Query()
    .Sort(BookCache.ByTitleComparer)
    .JoinWithAuthor()
    .ExecutePooled(0, 10);
```

Sorting before the join compares left values and the join fills the sorted rows (`SortBounded` then pages before joining, so only the page is joined). Sorting after the join compares `JoinResult<…>` rows and always runs the classic full sort.

## Resolver families (informational)

The generator picks a resolver based on the relationship shape; the call site is identical:

- **PK-to-PK** — left PK equals right PK. No FK declaration needed; useful for cross-cache views of the same key.
- **Right-unique-index** — right entity carries a unique FK back to the left (e.g. `Book → BookInfo` where `BookInfo.BookId` is `[DataCacheIndex(Unique)]`).
- **Left-symmetric-index** — left entity carries a symmetric many FK (`ManyToOne`); join walks the index's reverse map.
- **Left-symmetric-unique-index** — bijective 1:1 via a symmetric unique FK on the left.

All four use a paired-execution core that intersects candidate keys with index hits and dispatches a single bulk read into the right cache. Pairs are guarded by a leak-safety contract that returns rented arrays exactly once. Lower-level `JoinOne(...)` / `InnerJoinOne(...)` / `JoinMany(...)` / `InnerJoinMany(...)` extensions exist for the raw cache; pass the cache wrapper, not `.Query()`.

## Next

- [Prepared and Frozen Queries](prepared-queries.md) — joins on a frozen query fuse into the single pass.
- [Kafka integration](../advanced/kafka-integration.md) — wiring caches to topics so the join sides stay in sync.
- [Performance tuning](../advanced/performance-tuning.md) — hot-path guidance for joined queries.
