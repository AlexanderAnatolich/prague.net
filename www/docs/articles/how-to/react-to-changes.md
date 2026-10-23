---
title: React to Changes
description: Run code after a cache applies a live message with ICacheAfterHandler, handle each UpdateType correctly, and understand what makes a write Same rather than Update.
---

# React to Changes

**Goal:** maintain a derived structure (here, open order total per customer) that follows the cache as messages arrive, without polling.

**Prerequisite:** a cache registered through `AddKafkaCaches`. The code is in [`ReactToChanges.cs`](https://github.com/nanov/prague.net/blob/main/samples/Prague.Samples/HowTo/ReactToChanges.cs); the registration block is compiled-only, the rest runs as tests.

## 1. Implement the handler

`ICacheAfterHandler<TKey, TValue>.Handle` is called *after* the cache applied a message in the live phase. Every branch of `UpdateType` is reachable; the arguments differ per case.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/ReactToChanges.cs#react-handler)]

| `UpdateType` | `newValue` | `oldValue` | When |
|---|---|---|---|
| `Add` | set | null | key was not resident |
| `Update` | set | set | key was resident and `CacheEquals` said different |
| `Same` | set | null | key was resident and `CacheEquals` said equal |
| `Delete` | null | set | tombstone or a `treatAsDelete` filter, key was resident |
| `Filtered` | null | null | a filter skipped the message; `key` is `default` too |

The handler runs on the cache's own live worker, sequentially with the other handlers of that cache; exceptions are caught and logged, not propagated. The initial replay never calls it: when `Handle` runs for the first time, the cache already holds the topic.

## 2. Register it

Type registration makes it a singleton `ICacheAfterHandler<string, Order>`; the factory form is for handlers with constructor arguments DI cannot supply on its own.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/ReactToChanges.cs#react-register)]

## 3. Test it by hand

The consumer is the only caller in production; a test is the other one. Drive the sequence a topic would produce.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/ReactToChanges.cs#react-handler-test)]

## 4. Same versus Update

A replayed or duplicated message is common on Kafka. The cache compares the incoming value with the resident one using the generated `CacheEquals` (deep, property by property, collections included); an equal value is reported as `Same` and the resident instance is *not* swapped.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/ReactToChanges.cs#react-same-vs-update)]

`AddOrUpdate` returns `false` for `Same`, which is exactly the signal the live phase uses. [Conditional Updates](../core-concepts/conditional-updates.md) covers equality in depth.

## Pitfalls

- **Reading `key` on `Filtered`.** It is `default`, so for a string key it is `null`. Return early.
- **Awaiting I/O in `Handle`.** Every message of that cache waits behind it, and the liveness check reports `HandlerProcessingTimeout` after 5 s by default. Enqueue to a channel and return.
- **Expecting the replay.** If you need the derived structure to reflect the initial load, build it from the cache after `LoadingCompletion` and use the handler only for deltas.
- **Mutating `newValue`.** It is the instance the cache holds and every query returns. Treat it as read-only; `Clone()` it if you must change it (see [Map, Clone, Batch](map-clone-batch.md)).
