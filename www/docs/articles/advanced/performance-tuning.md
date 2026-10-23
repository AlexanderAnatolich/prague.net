---
title: Performance Tuning
description: Hot-path guidance for Prague - pooled results, frozen queries, index order and choice, SortBounded, static lambdas, statistics and benchmarks.
---

# Performance Tuning

Prague is engineered for low-latency reads under sustained write load. This page collects the guidance that lets you actually realize that on the hot path.

## Always use `ExecutePooled`

The pooled execute path uses `ArrayPool.Shared` to lease the result buffer; the allocating path goes through `new T[]`. Under a workload of 100k queries per second the difference is gen-0 GC pressure measured in megabytes-per-second vs. effectively zero.

```csharp
using var results = cache.Query().WithCustomerId(42).ExecutePooled();
```

`using` is non-negotiable — without disposal, the rented array is not returned to the pool and you regress to the allocating path's GC profile (worse, because the pool's internal bag keeps growing).

If you must hold results across an `await` (where `using` doesn't fit cleanly), either keep the `QueryResults<T>` in a field and dispose it explicitly, or copy out with `ToArray()` / `ToPooledArray()` (the latter rents from `PragueArrayPool<T>.Pool`; return it yourself) and dispose the results at once.

## Freeze hot, repeated shapes

A query that runs the same shape over and over belongs in a field as a frozen query. `Prepare<TArgs>()…BuildFrozen()` plans once, binds a `PointLookup` / `Pipeline` / `Replay` executor, and executes at 0 B from any thread — 3–36× over the eager builder on indexed shapes, 18 ns for a unique-key point lookup. Row order is not guaranteed unless you sort with a total comparer. See [Prepared and Frozen Queries](../core-concepts/prepared-queries.md).

## Index order matters on the eager path

The eager planner walks `WithXxx` / `UseIndex` calls in declaration order. Place the most selective index first to keep the intermediate set small:

```csharp
// Good — Email is unique, so we narrow to ≤1 candidate immediately
cache.Query()
    .WithEmail(target)                  // unique
    .WithCreatedAtUnixMs(q => q.Gte(t)) // range
    .ExecutePooled();

// Worse — the range walk produces many candidates, then we intersect with the unique hit
cache.Query()
    .WithCreatedAtUnixMs(q => q.Gte(t)) // range scan
    .WithEmail(target)                  // unique intersect
    .ExecutePooled();
```

There is no runtime cost-based optimizer on the eager path; the order in source is the order in execution. A frozen query seeds from the narrowest lane for each execution's arguments, whichever way round you declared them.

## Pick the right index variant

- **`Unique`** when the value is genuinely unique. The runtime can short-circuit after the first hit.
- **`Many`** when many entities share a value; the index returns a pooled span.
- **`Range`** when you need ordered traversal (`q => q.Gte(a).Lt(b)`). Backed by a pooled B+tree; walks are O(log n) to the first bound, then sequential.
- **`[DataCacheValueIndex(CompareOp.Equal, K)]`** when you only ever query `Property == K` for a small set of `K`s. The key set stores keys only and the lane is one set lookup.
- **`[DataCacheHasValueIndex]`** when you filter on "has a value at all" (nullable column, soft-delete flag). Same key-set mechanism.

## Conditional updates

`AddOrUpdate(value)` compares the incoming value to the resident one via `CacheEquals` and short-circuits to `UpdateType.Same` when they're equal — no index churn, no after-handler invocation. Mark monotonic-but-irrelevant fields with `[DataCacheIgnoreEquality]` so they don't force `Update` results on every emit. See [Conditional Updates](../core-concepts/conditional-updates.md).

## Static lambdas in join filters

Join filters and key selectors accept a `TArg` form that lets the JIT devirtualize the lambda:

```csharp
// Good — static lambda + arg, zero allocation per call
.JoinWithAuthor(static (q, status) => q.WithStatus(status), authorStatus)

// Worse — closure-capturing lambda; allocates each call
.JoinWithAuthor(q => q.WithStatus(authorStatus))
```

The lambda here is rebuilt on every query execution, so the capture costs an allocation per call. The same applies to join key selectors — prefer `static` lambdas that close over no state and take what they need as a `TArg`.

## Static lambdas in Kafka filters

The rationale is different on the ingestion side. A `WithKeyFilter` / `WithValueFilter` predicate is constructed **once**, while the handler is built, and held for the process lifetime — a capturing closure there allocates at startup, not per message. What you still want is a predicate that reads DI state without resolving or capturing anything on the hot path, and the state-factory overloads give you exactly that:

```csharp
// State resolved once, at build time; predicate stays static, so one delegate per process
builder.AddCache<OrderCache, string, Order>()
    .WithKeyFilter(
        static sp => sp.GetRequiredService<IAllowList>().Ids.ToFrozenSet(),
        static (allow, key) => allow.Contains(key));
```

State arrives as an argument rather than a capture, so the lambda can be `static`: Roslyn caches it in a static field and the ingestion path allocates nothing. See [Kafka Integration](kafka-integration.md) for the full set of overloads and their lifecycle caveats.

## Avoid `ToList()` / `ToArray()`

Both copy the pooled buffer into a regular allocation. Iterate the `QueryResults<T>` directly or use `AsSpan()` for span-based math:

```csharp
using var prices = orderCache.Query().WithCustomerId(42).ExecutePooled();
decimal sum = 0;
foreach (var o in prices.AsSpan())
    sum += o.Total;
```

For interop with `IEnumerable<T>` APIs, prefer `AsSpan()`-backed iteration over `ToList()` if the consumer accepts spans.

## Sort over candidates, not the cache — and page with `SortBounded`

Sorting runs after intersection on the candidate set, not on the full cache. Narrow before sorting, and use `SortBounded` for a page: it selects the top-k rows instead of sorting everything and is 2.3–8.6× faster than `Sort` when `take` is a small slice of the matches. Use `Sort` when you take a quarter or more of the result, or the whole of it. A `readonly struct` comparer makes either plan 0 B; a class comparer costs ~88–168 B per query.

```csharp
// Good — select a page of 25 from ~100 candidates
cache.Query().WithCustomerId(42).SortBounded(OrderCache.ByDateComparer).ExecutePooled(0, 25);

// Wasteful — full sort of the whole cache before slicing the top 25
cache.Query().Sort(OrderCache.ByDateComparer).ExecutePooled(0, 25);
```

See [Sorted Paging](../core-concepts/sorted-paging.md) for the measured crossover.

## Stats and observability

Each cache exposes `Statistics`:

```csharp
var stats = cache.Statistics;
Console.WriteLine($"size={stats.LiveSize} indices={stats.Indexes.Count}");
foreach (var (name, info) in stats.Indexes)
    Console.WriteLine($"  {name}: {info.LiveKeysSize}/{info.LiveValuesSize}");
```

`LiveSize` / `LiveKeysSize` / `LiveValuesSize` are read-through (no snapshot). For periodic monitoring, call `DataCacheStatisticsMarshall.TakeSnapshot(stats)` and read `stats.Size` / `stats.Indexes[name].KeysSize`.

On the Kafka side, `KafkaCachesStatistics` (injectable) holds one `KafkaCachesConsumerStatistics` per consumer (assigned partitions, broker latency, messages and bytes received, initial-load time) and, under it, one `KafkaDataCacheStatistics` per cache (topic name, size, messages received, fetch-queue depth). The OpenTelemetry integration in `Prague.Kafka.OpenTelemetry` exports these as metrics — register it on the meter provider: `.WithMetrics(m => m.AddPragueKafkaInstrumentation())`. The `GET /prague` endpoint of `Prague.Api` serves the same statistics as JSON. See [Kafka Integration](kafka-integration.md#opentelemetry-metrics).

## Benchmarks

The reference benchmarks live in `benchmarks/Prague.Benchmarks`. Run them as a sanity check on your hardware before drawing conclusions from production numbers:

```bash
dotnet run -c Release -f net9.0 --project benchmarks/Prague.Benchmarks -- --inProcess --anyCategories <category>
```

Run one category at a time with nothing else on the machine; rows in the 5–10 µs range move more than the deltas usually being claimed. Every measured table lives in `benchmarks/Prague.Benchmarks/RESULTS.MD`, and `perf/` holds a regression tripwire against per-machine baselines.

The headline reads/sec number (15.9M+) comes from the concurrent `HeavyJoinPooledBenchmarks` (10 reader threads + concurrent writers, pooled 3-level join) — it is the sum of result rows returned divided by wall-clock seconds. The `<100 ns` indexed-lookup figure is a single-key lookup on a single physical core; a frozen unique-key point lookup is 18 ns. All figures are from an Apple M4 Pro; absolute numbers vary by hardware, so scale them down for shared-tenancy machines.
