# Joins

> **Read when:** working on `JoinWith`/`JoinOne`/`JoinMany`, resolver families, the paired core, inner/chained joins, or join leak-safety.

Compile-time-safe joins over the query builder. Spans Core (resolvers, paired core) and Generated (T4 builders, FK convenience). Builds on [`query.md`](query.md) (builder/discriminators), [`indexes.md`](indexes.md) (which index drives which family), [`collections.md`](collections.md) (`ValueSet`/`JoinedKeyPair`/`PooledSet`). Naming note: legacy `JoinOneNew`/`JoinMany…New` lost the `New` suffix when the originals were retired — git history & some specs still say `…New`.

## Public surface

- **FK convenience (preferred):** `.JoinWith{T}()` / `.InnerJoinWith{T}()` — emitted from `[DataCacheForeignKey<T>]` (see `generated.md`). Three overloads each: no-filter, filter, filter + `TArg`.
- **Lower-level:** `.JoinOne(...)`, `.InnerJoinOne(...)` (1:1 / outer + inner), `.JoinMany(...)`, `.InnerJoinMany(...)` (1:N). **Pass the cache wrapper directly — no `.Query()` at the call site.** Extension parameter is the `IDataCache<…>` interface (so C# can infer `TRightValue`), cast to the concrete cache inside.
- Pooled join results: zero-allocation, caller `Dispose()`s. `result.Left`, `result.Right1` (1:1), `result.Right2` (1:N list), etc.

## Strategy structs (zero-cost, JIT-devirtualized per closed generic)

**Filter** — `IJoinOneFilter<TBuilder>.Apply(q)`:
- `NoFilter<TBuilder>` — identity, `Apply` fully elided.
- `JoinOneFilter<TBuilder>` — wraps `Func<TBuilder,TBuilder>`.
- `JoinOneFilterWithArg<TBuilder,TArg>` — wraps func + arg; **declare the lambda `static` for zero-alloc capture.**

**Key selector** — `IKeySelector<TIn,TOut>` (static-abstract `IsIdentity`, JIT-folded):
- `IdentitySelector<T>` — identity path uses `Unsafe.As<TIn,TOut>` reinterpret; `Select` elided. Default everywhere.
- `KeySelector<TIn,TOut>` — wraps `Func<TIn,TOut>`. Enables cross-key-type joins (e.g. `int → long`) with **no new resolver types**.
- `KeySelectorWithArg<TIn,TArg,TOut>` — func + arg; `static` lambda for zero-alloc.

Filter callbacks receive a `NonExecutableQuery<TRightCache>` discriminator (`WithXxx`/`UseIndex`/`Or` callable; `Execute*` hidden). `AsNonExecutable()` (internal extension) swaps `ExecutableQuery<TCache>` → `NonExecutableQuery<TCache>` preserving other generics — call `Cache.Query().AsNonExecutable()` in any resolver needing a non-exec builder.

## The four JoinOne resolver families

| Family | Trigger | Resolver | Notes |
|--------|---------|----------|-------|
| **PK-to-PK** | `TLeftKey == TRightKey` | `JoinOneResolver` | No index step; stays **unpaired**. Optional key selector for cross-key joins. |
| **Right-unique-index** (FK-on-right) | `[DataCacheIndex(Unique)]` on right FK → `CacheUniqueIndex<TRightKey,TRightValue,TLeftKey>` | `JoinOneRightUniqueIndexResolver` | e.g. `Book → BookInfo` where `BookInfo.BookId` is unique. |
| **Left-unique-index** (FK-on-left, 1:1) | `[DataCacheIndex(Unique, Symmetric=true)]` on left → `CacheSymmetricUniqueIndex` (`.Reverse` supports `IntersectValues`) | `JoinOneLeftUniqueIndexResolver` | Bijective, no fan-out. e.g. `Author.BookId`. |
| **Left-symmetric-index** | `[DataCacheIndex(Many, Symmetric=true)]` on left → `CacheSymmetricKeyValueListIndex` | `JoinOneLeftSymResolver` | Index-driven; fans out (many lefts share one index value). |

Each family has identity + key-selector overloads × {no-filter, filter, filter+arg}; identity overloads pass `IdentitySelector` (zero cost).

## Paired core (execution engine)

- `PairedCacheQueryBuilderCoreCombined<TLeft,TKey,TValue>` stores candidates as `ValueSet<JoinedKeyPair<TLeft,TKey>>` and slots in as `TExecutor` inside `CacheQueryBuilderCombined`. `JoinedKeyPair.Equals/GetHashCode` consider only `.Key` — intersection is by key alone.
- `ExecutePaired<TContainer>(ref container)` calls `_dataCache.TryGet<TLeft,TContainer>(ref container, ref _candidates, _filter)` — native paired bulk-read, no projection-to-unpaired, disposes candidates in finally.
- `UseIndex` on the paired core is intersect-only (pairs added once at promotion). Strategies per index type avoid temp `ValueSet`: `IncrementalIntersecter` (KeyValue), direct `IntersectWith` against the index's `PooledSet` (KeyValueList), `IntersectPairedViaTemp` only for Range/LastUpdated B-tree walks.
- **Filter executor-agnosticism:** `Where`/`UseIndex`/`WithXxx`/`Or` constrain on the **discriminator** (`IBaseFilterable`, `ICacheCarrier<TCache>`), never the executor type — so filter callbacks are unchanged whether a resolver runs paired or unpaired.
- **JoinOne LeftSym borrow-the-set (`JoinOneLeftSymResolver` only):** `TLeft = LeftKeySetView<TLeftKey>` wrapping a *borrowed* reference to the index's internal `PooledSet<TLeftKey>` (no copy, no side-map). `OuterFanOutContainer` / `InnerFanOutContainer` reinterpret it via `Unsafe.As` and iterate; inner additionally filters via `_candidates.Contains(lk)`. The JoinMany resolvers do NOT do this — they carry plain `TLeftKey` pairs and spread them over rounds (next section).

## JoinMany rounds

All three JoinMany resolvers (`JoinManyRightListIndexResolver`, `JoinManyLeftSymResolver`, `JoinManyCollectionResolver`) run the paired core with `TLeft = TLeftKey`, so the filter builder type is `PairedCacheQueryBuilderCoreCombined<TLeftKey, TRightKey, TRightValue>` for every JoinMany shape (hand-written extensions, T4 chained levels, codegen `JoinWith` alike). Because `JoinedKeyPair` identity is the right key alone, one pair set cannot hold `(L1, r)` and `(L2, r)`; lefts sharing rights — a LeftSym lookup group, a non-injective key selector folding several groups onto one right bucket, overlapping M:N collection buckets — are handled by **rounds**:

- `JoinManyRounds<TLeftKey,TRightKey>` (`src/Prague.Core/QueryBuilders/JoinManyRounds.cs`, internal `ref struct`) — round 0 is an inline `ValueSet<JoinedKeyPair<…>>` sized for the whole join; further rounds are the cold path (created on the first right-key collision, `NoInlining`) and live in a `ValueSet[]` rented from `PragueArrayPool`, grown by rent-copy-return, returned cleared on `Dispose`. `Add(pair, startRound)` first-fits the pair into the earliest round `>= startRound` whose set does not contain that right key — `ValueSet.Add` returning `false` IS the membership test, so right uniqueness per round holds by construction whatever the start hint. The unhinted `Add(pair)` exploits that the rounds holding a key form a prefix `[0, k)`: it binary-searches the first free round with the pair hashed once (`ValueSet.HashOf` + the hash-taking `Contains`/`Add` overloads), so a right shared by m lefts costs O(m log m) probes, not m²/2. `this[i]` hands a round out by `ref`; `Count` is the number of rounds in use. The single-round case never touches the heap.
- **Exactness invariant:** each left's right bucket (`GetValuesUnsafe` — never null, `Empty` sentinel on a miss) is enumerated exactly once; every enumerated right becomes a pair, and `container.Init(left, pairsRecorded)` runs only when `pairsRecorded > 0`. A slot thus reserves exactly the Adds the rounds can deliver to it, whatever the index writer does concurrently — no live left-key set is read at Add time any more.
- **Start-round hint (LeftSym):** a pooled `ValueDictionary<TRightIndexKey,int>` (disposed in `finally`) counts the lefts already processed per right index key (`Dispose(withValues: true)` in the `finally` — the parameterless `Dispose` keeps the pooled values array for hand-off); the j-th left of a group starts first-fit at round j — its rights are the same bucket the earlier j lefts already placed in rounds 0..j-1, so probing those would only fail (m lefts × n shared rights: O(m·n) instead of O(m²·n)). Keyed by the *right index key* so the groups a non-injective selector folds together share one counter; collisions from other groups just push individual pairs further. The collection resolver has no natural counter key (a per-right map would need a growable dictionary to stay safe under concurrent owner additions) and relies on the unhinted binary-searching `Add(pair)` instead: an owner shared by 4096 elements went from 8.4M rejected probes (each boxing the int right key: 201 MB and 38 ms per query) to 4096 hashes.
- **Execution order:** record every left → `PrepareSharedBuffer()` → `RegisterPooledBuffer` (BEFORE any user code runs, so a throw mid-round cannot strand the rental; `ExecuteReverseMany` passes an inert `default(QueryResultsDisposer)`) → per non-empty round: build the paired core over the round's set, `_filter.Apply`, overwrite the round slot with `default`, `ExecutePaired(ref container)`. Inner paths keep the empty-candidates early return, the "no pairs ⇒ `candidates.IntersectWith(empty)`" narrow, slot materialisation via `GetValueRefOrAddDefault` before `Init`, and `RetainNonEmptyManySlots` after the rounds.
- **Per-round hand-off discipline (the rounds' form of the `handedOff` guard):** `ExecutePaired` disposes the set it is given, so the resolver zeroes the round slot *after* `_filter.Apply` and *immediately before* `ExecutePaired`. A throw inside the user lambda leaves the set owned by `JoinManyRounds.Dispose()` (run from `finally`); a set that reached the paired core is skipped there. Same load-bearing position as the JoinOne `handedOff = true`.
- **Consequences for users:** the join filter lambda runs **once per round** — keep it pure and cheap to re-run; rights inside a slot come out round-major (results are unordered sets anyway). A right whose bucket membership is removed and re-added while its left's bucket is being walked can be yielded twice by the bucket enumerator and is then recorded and delivered twice to that left (the slot reserved both, so nothing overflows) — RightList collapses such a repeat through its single pair set, rounds do not. `JoinManyCollectionResolver` needs only the index half that answers "rights for a left" (`index.Forward` for element→owners, `index.Reverse` for owner→referenced); its ctor takes just that half.

## Inner joins — unified post-walk

`InnerJoinOne` at level 0 constructs the resolver with `isInner: true`. `UnsafeExecuteIndexedInner` flow (PK-to-PK, RightUnique, LeftUnique — **LeftSym deferred**):
1. seed `ValueSet<JoinedKeyPair>` from candidates (`PrepareIndexedInner` = `_ = leftQuery.GetCandidates<TLeftKey>()` to trigger auto-populate-from-leftCache — note: direct `.Candidates.Count` access *bypasses* auto-populate, which was the historic PK-to-PK bug);
2. `Filter.Apply` (configures predicate, narrows candidates via `UseIndex`);
3. `ExecutePaired(ref container)` — `container.Add` for matches only;
4. `accessor.RetainNonNullSlots<TLeftKey,TRightValue>(ref candidates)` — drops `_results` entries whose Right_N slot is null/default (miss or predicate-reject) and narrows candidates. A slot is non-null **IFF** this resolver wrote it via `container.Add`; the `is null` check covers ref types and `Nullable<T>` uniformly.

This replaced the old miss-callback infra (`IInnerJoinContainer`, `UnsafeInnerResolverContainer`, etc., ~550 lines removed): 1 pair walk instead of 2, sequential filter walk instead of per-miss hash-removes.

## Chained joins

`.JoinWith{A}().JoinWith{B}()` (and `JoinOne` equivalents) — T4 Phase 1B emits chained levels for **PK-to-PK / RightUnique / LeftUnique** identity families (LeftSym chained deferred). Correctness uses the same post-walk `RetainNonNullSlots`.

## Leak-safety (`handedOff` guard) — all 4 families

Every `ExecuteReverse` / `UnsafeExecuteIndexedInner` wraps its `ValueSet<JoinedKeyPair<…>>` in `try { … } finally { if (!handedOff && pairs.IsInitlized) pairs.Dispose(); }`. **`handedOff = true` is set immediately before `ExecutePaired`, *after* `Filter.Apply`** — this position is load-bearing: `Filter.Apply` (user lambda) sits between paired-core construction and `ExecutePaired` and can throw; flipping earlier silently leaks the rented array. Enforces exactly-one-Dispose (a double `ArrayPool.Return` is swallowed by `ValueSet.Dispose` but corrupts the pool).

The joined pipeline itself (`ExecuteCoreJoined` / `CountCoreJoined` / `ExecuteCoreJoinedTop` / `ExecuteCoreJoinedKeyed`) releases a candidate set the indexed-inner phase seeded or auto-populated when a resolver throws before base execution (`ReleaseUnconsumedCandidates`, no-op after a legitimate consume because base execution disposes candidates by ref).

## Key files & tests

- `src/Prague.Core/QueryBuilders/JoinOneResolver.cs`, `…/CacheQueryBuilder.JoinOne.Extensions.cs`, `…/JoinManyResolver.cs`, `…/JoinManyCollectionResolver.cs`, `…/JoinManyRounds.cs`, `…/CacheQueryBuilder.JoinMany.Extensions.cs`, `…/CacheQueryBuilder.JoinManyCollection*.Extensions.cs`, `CacheQueryBuilder.cs` (paired core + `AsNonExecutable`).
- T4: `JoinQueryBuilders.tt`, `JoinResults.tt`.
- Tests: `tests/Prague.Core.Tests/Join/` (raw POCO, no codegen — `JoinOneCoreTests`, `…RightUniqueIndexCoreTests`, `…LeftUniqueIndexCoreTests`, `…SymIndexCoreTests`, `…KeySelectorCoreTests`, `…ChainedCoreTests`); `Prague.Generated.Tests.Join` (through codegen). JoinMany rounds: `JoinManyRoundsCoreTests` (engine unit tests, multi-round correctness, filter-once-per-round), `JoinManyNonInjectiveSelectorCoreTests` (LeftSym via rounds; RightList current behaviour pinned), `JoinManyLeftSymCollectionConcurrentMutationTests` / `JoinManyRightListIndexConcurrentMutationTests` (deterministic writer interleavings + 2 s live-writer stress), `Leaks/JoinManyRoundsLeakTests` and `Leaks/QueryJoinLeakTests` (pool balance incl. filter/predicate throwing mid-round).
