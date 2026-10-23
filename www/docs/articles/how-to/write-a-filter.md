---
title: Write a Filter
description: Keep unwanted Kafka messages out of a cache with header, key and value filters, turn soft deletes into removals, and give a predicate DI state.
---

# Write a Filter

**Goal:** decide per message whether it enters the cache, before it costs you an index write, and without a consumer group per tenant.

**Prerequisite:** a cache registered through `AddKafkaCaches` ([Kafka Integration](../advanced/kafka-integration.md)). Filters are declared on the handler builder, so each block below is a compiled registration method; the one test builds the container with each of them. The code is in [`WriteAFilter.cs`](https://github.com/nanov/prague.net/blob/main/samples/Prague.Samples/HowTo/WriteAFilter.cs).

## 1. Header filters: cheapest, run first

Header filters see only the raw headers; neither the key nor the value has been deserialized yet. A string value is compared as UTF-8 with no allocation; `WithHeaderFilter<T>` decodes the header with MessagePack and hands you the value.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/WriteAFilter.cs#filter-header)]

`passOnNull` decides what happens when the header is missing (`false`: reject). Tombstones are exempt from `WithHeaderExistsFilter`, so a delete without headers still removes the row.

## 2. Key and value filters

These run after the respective deserialization step, on the single consume thread.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/WriteAFilter.cs#filter-key-value)]

A tombstone (null value) skips key and value filters entirely and is applied as a delete.

## 3. Turn a soft delete into a removal

By default a rejected message is *skipped*: the cache keeps whatever it had. With `treatAsDelete: true` a rejection removes the resident row instead, which is how a `Status = Cancelled` message becomes a tombstone for the cache.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/WriteAFilter.cs#filter-treat-as-delete)]

## 4. Predicates with DI state

Two overloads. The factory form runs once when the handler is built and passes the result to every call: use it for a snapshot you want frozen (a `FrozenSet`, a compiled regex). The `TService` form resolves the service once and passes it to the predicate, so the predicate reads whatever the service holds at the time of the message.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/WriteAFilter.cs#filter-allow-list-service)]

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/WriteAFilter.cs#filter-di-state)]

## 5. Composition order

Filters AND-compose: a message must pass all of them. The *first* filter that rejects decides the outcome, so a `treatAsDelete` filter placed after a plain one never gets to delete what the plain one already skipped.

[!code-csharp[](../../../../samples/Prague.Samples/HowTo/WriteAFilter.cs#filter-first-reject-wins)]

## What a rejection looks like downstream

| Phase | Skip | Delete (`treatAsDelete`) |
|---|---|---|
| Initial load | message dropped | resident row removed, if any |
| Live | `ICacheAfterHandler` gets `UpdateType.Filtered` with no key and no values | `UpdateType.Delete` with the old value, if a row was resident |

The decision logic is in [Kafka message filters](https://github.com/nanov/prague.net/blob/main/context/kafka-filters.md).

## Pitfalls

- **Expensive predicates.** They run on the consume thread for every message, including the initial replay. Keep them to a dictionary lookup or a comparison; move anything heavier to an [after-handler](react-to-changes.md).
- **Filtering on something that changes.** If a row entered the cache and a later message for the same key is rejected with the default *skip*, the stale row stays. Use `treatAsDelete` when the predicate expresses "should not be here".
- **`WithHeaderExistsFilter` and tombstones.** Tombstones pass it. If you need to drop tombstones without a header, do it with a key filter, which tombstones also bypass; in other words, you cannot filter a tombstone by content, only by header value.
