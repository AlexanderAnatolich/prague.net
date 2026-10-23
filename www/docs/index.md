---
title: Prague Documentation
description: Documentation for Prague, a compile-time-safe in-memory event cache for .NET fed by Kafka topics, with indexed queries, joins and prepared queries.
---

# Prague

**Prague** (`Prague`) is a compile-time-safe, in-memory event cache for .NET (`net9.0` and `net10.0`), designed for event-sourced data streams from Kafka.

- **Zero-reflection, compile-time-safe** fluent query API powered by Roslyn source generators.
- **Three primary index types** — `Unique`, `Many`, `Range` — plus symmetric variants and predicate key-set indices, with automatic intersection and short-circuit on the first empty lane.
- **Compile-time joins** (1:1 and 1:N) driven by `[DataCacheForeignKey<T>]`, with pooled, zero-allocation result sets.
- **Prepared and frozen queries** — `BuildFrozen()` plans once and executes a single-pass pipeline at 0 B per execution, 3–36× over the eager builder on indexed shapes.
- **Kafka integration** with header/key/value filters, change detection via generated deep structural equality, split liveness/readiness health checks and OpenTelemetry metrics.
- **Hot paths**: `Span<T>`, `stackalloc`, `ArrayPool<T>`, SIMD bitmap unions, lock-free readers. 15.9M+ reads/sec (concurrent pooled join benchmark), <100 ns indexed lookup, 18 ns frozen unique-key point lookup (Apple M4 Pro).

## Start here

- [**Introduction**](articles/getting-started/introduction.md) — what Prague is and isn't.
- [**Installation**](articles/getting-started/installation.md) — packages and project setup.
- [**Quick Start**](articles/getting-started/quick-start.md) — a working cache in three steps.

## Core concepts

- [**Index Types**](articles/core-concepts/index-types.md) — the menu of secondary indices and predicate-based key-set indices.
- [**Query Engine**](articles/core-concepts/query-engine.md) — fluent API, intersection rules, `Or`, `Where`, pooled vs allocating execution, result mapping.
- [**Sorted Paging**](articles/core-concepts/sorted-paging.md) — `Sort` vs `SortBounded`: which plan to pick for a page and why ties matter.
- [**Prepared and Frozen Queries**](articles/core-concepts/prepared-queries.md) — `Prepare()…Build()` / `BuildFrozen()`: describe once, plan once, execute at 0 B; the ordering contract.
- [**Conditional Updates**](articles/core-concepts/conditional-updates.md) — how `CacheEquals` drives the `UpdateType` reported to downstream handlers.
- [**Joins**](articles/core-concepts/joins.md) — `JoinWith{Other}` / `InnerJoinWith{Other}` across foreign keys.

## Advanced

- [**Kafka Integration**](articles/advanced/kafka-integration.md) — topic settings, wiring caches to topics, filters, after-handlers, producer, health checks, OpenTelemetry.
- [**Global Last Update Index**](articles/advanced/global-last-update-index.md) — cross-cache change tracking for incremental sync.
- [**Performance Tuning**](articles/advanced/performance-tuning.md) — hot-path guidance.

## API Reference

The [API browser](api/index.md) is generated from XML doc comments on `Prague`, `Prague.Core`, `Prague.Kafka`, `Prague.Attributes`, and `Prague.Codegen`.
