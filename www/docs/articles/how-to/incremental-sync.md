---
title: Incremental Sync
description: Answer "what changed since my last watermark" from a global last-update index, across the customer and order caches, with the right handling of ties and deletes.
---

# Incremental Sync

**Goal:** a client calls with the watermark you handed it last time and gets only the customers whose data changed since, including changes that happened on their orders, plus a new watermark.

**Prerequisite:** `Customer` declares `CustomerActivityIndex`, an `IDataCacheGlobalLastUpdateIndex<int>` keyed by customer id, and `Order` reports into it through `CustomerId`. How that is declared, and what the index stores, is in [Global Last-Update Index](../advanced/global-last-update-index.md). The code is in [`IncrementalSync.cs`](https://github.com/nanov/prague.net/blob/main/samples/Prague.Samples/HowTo/IncrementalSync.cs); every block runs as a test.

## 1. Single cache: `UpdatedAfter`

On the cache whose primary key *is* the group key (`Customer`), the query builder exposes `UpdatedAfter`. The `out long max` overload gives you the next watermark in the same pass.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/IncrementalSync.cs#sync-updated-after)]

`UpdatedAfter` is strictly greater-than, so echoing the watermark back yields nothing until something changes. The timestamp is the one passed to `AddOrUpdate`; on the Kafka path that is the message timestamp.

## 2. An order write bumps its customer

This is the point of a *global* index: writing `ORD-003` for customer 42 moves customer 42's timestamp even though the `Customer` row did not change.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/IncrementalSync.cs#sync-order-write-bumps-customer)]

## 3. Cross-cache response

`Order` cannot call `UpdatedAfter` (its key is not the group key), so the sync reads the index directly: `GetValuesGt` returns the changed customer ids, each cache is then read by key or by lane, and `TryGetMax` over the returned ids yields the watermark.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/IncrementalSync.cs#sync-response)]

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/IncrementalSync.cs#sync-cross-cache)]

`CrossCacheSyncRoundTrips` checks the three states a client sees: a first call with watermark 0 returns everyone, an idle call returns nothing and the same watermark, and a call after one order write returns that one customer with all her orders.

## 4. Deletes

Two cases, and they differ:

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/IncrementalSync.cs#sync-deletes)]

- Removing one of several entities in a group **bumps** the group to the delete timestamp, so the client re-reads the customer and sees the shorter order list.
- Removing the **last** entity drops the group from the index. The deletion is invisible to `GetValuesGt`, so a client cannot learn about it from the watermark alone. If your protocol must propagate "customer 7 is gone", keep a tombstone list or compare id sets on the client.

## 5. Prepared form

The same narrowing exists on `Prepare<TArgs>()`, with the watermark as the argument.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/IncrementalSync.cs#sync-prepared)]

## Pitfalls

- **Wall-clock timestamps in tests.** `AddOrUpdate(entity)` stamps `UtcNow`; two writes in the same millisecond tie. Pass explicit timestamps when the order matters, as the tests do.
- **Equal timestamps at the watermark.** `UpdatedAfter` is exclusive, so a change stamped exactly at the watermark you return is not delivered next time. Message timestamps make this rare but not impossible; if you need at-least-once, subtract one millisecond from the watermark you hand out and dedupe on the client.
- **Watermark from a filtered subset.** `TryGetMax(ids, out max)` over the ids you actually return is right; the global maximum of the index would skip changes in groups you filtered out this round.
