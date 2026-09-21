# Kafka message filters

> **Read when:** adding/changing header/key/value filters or the `FilterDecision` skip-vs-delete logic.

Filter types live under `src/Prague.Kafka/Filters/`. `KafkaCacheHandlerBuilder` builder methods, all **AND-composed** across calls:

- `WithHeaderFilter(...)` — evaluated **first**, in the raw consume loop via `KafkaCacheHandler.IsHeaderFiltered(in RawHeaders)` against UTF-8 name/value **spans** (before key deserialization), returns `bool`. **No `treatAsDelete`.** It also self-filters the producer-instance header (`KafkaCaches.ProducerInstanceIdHeaderName` == this instance's id) so a producer never re-consumes its own writes.
- `WithKeyFilter(Func<TKey,bool>, bool treatAsDelete = false)`
- `WithValueFilter(Func<TValue,bool>, bool treatAsDelete = false)`

DI-aware variants, all resolving **once** at `Build` (see "Snapshot filters" below):

- `WithKeyFilter<TState>(Func<IServiceProvider,TState> stateFactory, Func<TState,TKey,bool>, bool treatAsDelete = false)`
- `WithValueFilter<TState>(Func<IServiceProvider,TState> stateFactory, Func<TState,TValue,bool>, bool treatAsDelete = false)`
- `WithKeyFilter<TService>(Func<TService,TKey,bool>, bool treatAsDelete = false)` — sugar; `TService` **never infers**, always spell it
- `WithValueFilter<TService>(Func<TService,TValue,bool>, bool treatAsDelete = false)` — same
- `WithHeaderFilter<TState,THeaderValue>(string name, Func<IServiceProvider,TState>, Func<TState,THeaderValue,bool>, bool passOnNull = true)` — neither type arg infers

No-filter path is zero-alloc (inline `IsEmpty` check). A thrown predicate is caught at the `DispatchRaw` call site, logged via `LoggerMessage`, and treated as **reject** (maps to `Skip`, never `Delete`).

## Snapshot filters — one ordered factory list

The builder holds `List<Func<IServiceProvider, KafkaKeyFilter<TKey>>>` (same for value; header holds a dict of factory lists). **Eager and DI-aware registrations share ONE list** — evaluation is first-reject-wins and the *rejecting* filter's own `TreatAsDelete` picks `Skip` vs `Delete`, so two lists concatenated at `Build` would silently reorder the chain. `FilterRegistrationOrderTests` guards this; nothing else does.

`Build` resolves the list through `internal BuildKeyFilters/BuildValueFilters/BuildHeaderFilters(sp)` — also the seam that makes broker-free filter unit tests possible (`tests/Prague.Kafka.Tests/DependencyInjection/Filter*Tests.cs`).

`KafkaKeyStatePredicateFilter<TState,TKey>` / `KafkaValueStatePredicateFilter<TState,TValue>` hold `(state, predicate, treatAsDelete)` and call `_predicate(_state, key)`. State is passed as an **argument, not captured**, so the predicate can be `static` → Roslyn caches it in a static field → one delegate per process, zero allocation on the ingestion path. Header state filters instead bind the state into a closure at `Build` (one allocation, once) to avoid duplicating `KafkaHeaderPredicateFilter`'s deserialization ladder.

Inject several services with a **named tuple** — element names survive into the predicate:

```csharp
.WithKeyFilter(
  static sp => (allow: sp.GetRequiredService<IAllowList>(), clock: sp.GetRequiredService<IClock>()),
  static (s, key) => s.allow.Contains(key) && s.clock.IsOpen)
```

`sp` is the **root** provider (`KafkaCacheHandlers` is a keyed singleton). A scoped service passed to the `TService` overloads throws at `Build` via `ResolveFilterService<TService>` — the container will not catch it, because a default `BuildServiceProvider()` hands scoped services out of the root silently and MS only errors under `validateScopes: true` (Development only).

**Startup ordering:** `Build` — and therefore every state factory — runs during hosted-service *construction*, before the `StartAsync` of **every** hosted service. A state service that populates itself in its own `StartAsync` is empty when the factory runs. (It is the initial *load* that is ordered by registration, inside `KafkaCachesBackgroundWorker.StartAsync`.)

## FilterDecision (key + value share it)

`FilterDecision { Accept, Skip, Delete }` (`Filters/FilterDecision.cs`). Aggregates `KafkaKeyFilters<TKey>.Evaluate(key)` / `KafkaValueFilters<TValue>.Evaluate(value)` return it; each concrete filter carries `internal abstract bool TreatAsDelete`. **First-reject-wins** — the first rejecting filter's flag picks `Delete` vs `Skip`:

- `Skip` → silent drop on load / live publishes `RAW_KIND_FILTERED` → after-handlers fire with `UpdateType.Filtered`.
- `Delete` → live publishes `RAW_KIND_DELETE` → `HandleRawLiveDelete` (removes key, fires `UpdateType.Delete` with old value only if key was present) / on load `RemoveDuringLoad` cancels any buffered value **and** removes the key from the cache (no after-handler). Buffer-only was #35: the compacting buffer is flushed mid-load, so once a key's value had reached the cache the delete was silently lost.
