namespace Prague.Core;

using System.Buffers;
using System.Runtime.CompilerServices;
using Prague.Core.Collections;
using Prague.Core.TypeSystem;
using Prague.Core.Utils;

// ── JoinMany M:N collection resolver ──────────────────────────────────────
//
// Collection join over one half of a symmetric collection index built on the
// cache that owns the List<TKey> property. Given a Tag cache (left) and a Book
// cache (right) where Book carries List<int> TagIds, each tag fans out to the
// SET of books whose TagIds contain it (reverse direction); read the other way,
// each book fans out to the tags it lists (forward direction). Both are M:N —
// a book has many tags, a tag has many books — so the same right key legitimately
// appears under several lefts and the plain right-key-identity pair set cannot
// hold every (left, right) combination. The resolver first-fits every pair into
// JoinManyRounds and runs one paired execute per round; each left's bucket is
// enumerated exactly once and Init(left) receives exactly the number of pairs
// recorded for it, so a slot can never receive more Adds than it reserved. Pairs are
// not deduplicated within a left: an owner whose bucket membership is removed and
// re-added while that left's bucket is being walked can be yielded twice by the
// enumerator, is recorded twice (the second copy lands in a later round) and is
// delivered twice — the slot reserved both, so the invariant holds.
//
// The index is the symmetric collection index over the owning cache:
//   Forward : tagId    → {bookKeys}  (CacheKeyValueListIndex<TRightKey, _, TLeftKey>)
//   Reverse : bookKey  → {tagIds}    (CacheKeyValueListIndex<TLeftKey,  _, TRightKey>)
// The resolver only needs the half that answers "rights for a left".

/// <summary>
/// Resolver for an M:N collection JoinMany over one half of a
/// <see cref="CacheCollectionSymmetricKeyValueListIndex{TKey,TValue,TIndexKey}"/>. For each left
/// key the index maps it to the set of right keys; each (left, right) pair is first-fitted into a
/// round, the slot is sized from the pairs recorded, and every round runs its own paired execute.
/// The user filter therefore runs once per ROUND (and must stay pure); rights inside a slot come
/// out round-major.
/// </summary>
/// <typeparam name="TLeftKey">Left cache's key type — the index half's lookup key.</typeparam>
/// <typeparam name="TLeftValue">Left cache's value type.</typeparam>
/// <typeparam name="TRightCache">Right cache wrapper.</typeparam>
/// <typeparam name="TRightKey">Right cache's key type — the index half's value key.</typeparam>
/// <typeparam name="TRightValue">Right cache's value type (also the inner type of QueryResults).</typeparam>
/// <typeparam name="TOwnerValue">The index half's (phantom) value type — the cache owning the collection property.</typeparam>
/// <typeparam name="TFilter">Filter strategy struct over the paired non-executable builder.</typeparam>
public struct JoinManyCollectionResolver<TLeftKey, TLeftValue, TRightCache, TRightKey, TRightValue, TOwnerValue, TFilter>
	: IJoinManyResolver<TLeftKey, TLeftValue, TRightValue>
	where TLeftKey : notnull, IEquatable<TLeftKey>
	where TRightKey : notnull, IEquatable<TRightKey>
	where TLeftValue : ICacheEquatable<TLeftValue>, ICacheClonable<TLeftValue>
	where TRightValue : ICacheEquatable<TRightValue>, ICacheClonable<TRightValue>
	where TRightCache : IDataCache<TRightCache, TRightKey, TRightValue>
	where TFilter : struct, IJoinFilter<
		CacheQueryBuilderCombined<NonExecutableQuery<TRightCache>, PairedCacheQueryBuilderCoreCombined<TLeftKey, TRightKey, TRightValue>, TRightKey, TRightValue, Resolvers<BaseResolver<TRightKey, TRightValue>>, TRightValue>> {

	// ── Fields ───────────────────────────────────────────────────────────────
	//
	// Both join directions are the same algorithm over one half of a symmetric collection index;
	// they only differ in which half answers "rights for a left":
	//   reverse  (element→owners, e.g. tag→books): rightsIndex = Forward
	//   forward  (owner→referenced, e.g. book→tags): rightsIndex = Reverse
	// TOwnerValue is the index half's (phantom) value type — never materialised; only GetValuesUnsafe
	// is called.
	private readonly CacheKeyValueListIndex<TRightKey, TOwnerValue, TLeftKey> _rightsIndex;
	private readonly TRightCache _rightCache;
	private TFilter _filter;
	private readonly bool _isInner;

	static JoinManyCollectionResolver() {
		SlotCloner<QueryResults<TRightValue>>.Register(
			static (ref QueryResults<TRightValue> v) => v.CloneElements());
	}

	internal JoinManyCollectionResolver(
		CacheKeyValueListIndex<TRightKey, TOwnerValue, TLeftKey> rightsIndex,
		TRightCache rightCache,
		TFilter filter,
		bool isInner = false) {
		_rightsIndex = rightsIndex;
		_rightCache = rightCache;
		_filter = filter;
		_isInner = isInner;
	}

	// ── Static / property values ─────────────────────────────────────────────

	public static bool IsSorter { get; } = false;
	public bool Inner => _isInner;

	// ── Clone / CloneValue ───────────────────────────────────────────────────

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void Clone<TFullResult>(int index, ref TFullResult value) where TFullResult : struct, IJoinResult {
		ref var item = ref value.TUnsafeGetValAt<QueryResults<TRightValue>>(index);
		item.CloneInPlace();
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public static void CloneValue(ref QueryResults<TRightValue> value) => value.CloneElements();

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public void Clone(ref QueryResults<TRightValue> value) => CloneValue(ref value);

	// ── Pair recording ───────────────────────────────────────────────────────

	/// <summary>Resolves a left to its right bucket; false when the bucket is empty.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private readonly bool TryGetBucket(TLeftKey leftKey, out PooledSet<TRightKey, DefaultKeyComparer<TRightKey>> bucket) {
		bucket = _rightsIndex.GetValuesUnsafe(leftKey);
		return bucket.Count > 0;
	}

	/// <summary>
	/// Enumerates <paramref name="bucket"/> once, first-fitting every (left, right) pair into
	/// <paramref name="rounds"/>; returns the number of pairs recorded — the exact capacity the
	/// left's slot must reserve. Right multiplicities across lefts are small for collection FKs,
	/// so first-fit always starts at round 0. A right the enumerator yields twice (removed and
	/// re-added under the walk) is recorded twice.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int RecordPairs(ref JoinManyRounds<TLeftKey, TRightKey> rounds, TLeftKey leftKey,
		PooledSet<TRightKey, DefaultKeyComparer<TRightKey>> bucket) {
		var added = 0;
		foreach (var rightKey in bucket) {
			rounds.Add(new JoinedKeyPair<TLeftKey, TRightKey>(leftKey, rightKey));
			added++;
		}
		return added;
	}

	// ── Round execution ──────────────────────────────────────────────────────

	/// <summary>
	/// Runs one paired execute per non-empty round: builds the paired core over the round's pair
	/// set, applies the user filter, then hands the set to <c>ExecutePaired</c>, which disposes it.
	/// The round's slot is overwritten with <c>default</c> right before the hand-off — after the
	/// filter ran — so a throw inside the user lambda leaves the set to
	/// <see cref="JoinManyRounds{TLeftKey,TRightKey}.Dispose"/>, while a set that reached the
	/// paired core is never disposed twice.
	/// </summary>
	private void RunRounds<TContainer>(ref JoinManyRounds<TLeftKey, TRightKey> rounds, ref TContainer container)
		where TContainer : struct, IJoinedResultContainer<TLeftKey, TRightValue>, allows ref struct {
		var dataCache = _rightCache.Cache;
		var count = rounds.Count;
		for (var i = 0; i < count; i++) {
			ref var pairs = ref rounds[i];
			if (pairs.Count == 0) {
				continue;
			}

			var pairedCore = new PairedCacheQueryBuilderCoreCombined<TLeftKey, TRightKey, TRightValue>(dataCache, pairs);
			var builder = new CacheQueryBuilderCombined<
				NonExecutableQuery<TRightCache>,
				PairedCacheQueryBuilderCoreCombined<TLeftKey, TRightKey, TRightValue>,
				TRightKey, TRightValue,
				Resolvers<BaseResolver<TRightKey, TRightValue>>,
				TRightValue>(
				new NonExecutableQuery<TRightCache>(_rightCache),
				pairedCore,
				new Resolvers<BaseResolver<TRightKey, TRightValue>>(new BaseResolver<TRightKey, TRightValue>()),
				0);

			// NoFilter.Apply() is JIT-elided. Filter narrows pairs but cannot add.
			// A throw here (user lambda) must leave disposal to the rounds.
			builder = _filter.Apply(builder);

			pairs = default;
			Unsafe.AsRef(in builder._leftQuery).ExecutePaired(ref container);
		}
	}

	// ── Core outer execution ─────────────────────────────────────────────────

	/// <summary>
	/// Walks input lefts: per left, first-fit its (left, right) pairs into rounds and <c>Init</c> the
	/// slot with the pairs recorded; then <c>PrepareSharedBuffer</c>, register the pooled buffer
	/// with <paramref name="disposer"/> (before any user code can throw) and run the rounds.
	/// </summary>
	private void ExecuteOuter<TContainer>(ref TContainer container, ReadOnlySpan<TLeftKey> leftKeys,
		ref QueryResultsDisposer disposer)
		where TContainer : struct, IJoinedKeyedResultContainer<TLeftKey, TRightValue>, allows ref struct {

		if (leftKeys.IsEmpty)
			return;

		var rounds = new JoinManyRounds<TLeftKey, TRightKey>(leftKeys.Length);
		try {
			foreach (var leftKey in leftKeys) {
				if (!TryGetBucket(leftKey, out var bucket))
					continue;

				var added = RecordPairs(ref rounds, leftKey, bucket);
				if (added > 0)
					container.Init(leftKey, added);
			}

			// PrepareSharedBuffer allocates the contiguous TRightValue[] partitioned per left;
			// register it for pooled return BEFORE running rounds so a throw mid-round cannot
			// strand the rental.
			container.PrepareSharedBuffer();
			RegisterPooledBuffer(ref disposer, container.GetSharedBuffer());

			RunRounds(ref rounds, ref container);
		}
		finally {
			rounds.Dispose();
		}
	}

	void IJoinResolver.UnsafeExecuteWithAccessor<TAccessor>(
		ref TAccessor accessor, bool cloneOnAdd, bool shouldPool, ref QueryResultsDisposer disposer) {
		// Pool the per-left child buffer only when a disposer exists to return it (pooled execution).
		var inner = new InnerKeyedContainer<TAccessor>(accessor, cloneOnAdd, disposer.IsActive);
		ExecuteOuter(ref inner, accessor.GetKeys<TLeftKey>(), ref disposer);
	}

	void IJoinManyResolver<TLeftKey, TLeftValue, TRightValue>.ExecuteReverseMany<TContainer>(
		ref TContainer container, ReadOnlySpan<TLeftKey> keys) {
		// The caller owns the container's buffer; an inert disposer makes registration a no-op.
		var disposer = default(QueryResultsDisposer);
		ExecuteOuter(ref container, keys, ref disposer);
	}

	// Register the rented contiguous child buffer for return to the pool on result Dispose.
	private static void RegisterPooledBuffer(ref QueryResultsDisposer disposer, TRightValue[]? buffer) {
		if (disposer.IsActive && buffer is { Length: > 0 })
			disposer.AddPooledBuffer(buffer);
	}

	// ── IndexedInner ────────────────────────────────────────────────────────

	void IJoinResolver.PrepareIndexedInner<TExecutor>(
		ref TExecutor leftQuery, bool cloneOnAdd, bool shouldPool, ref QueryResultsDisposer disposer) {
		_ = leftQuery.GetCandidates<TLeftKey>();
	}

	/// <summary>
	/// Inner-join attach path: the same round-building flow as <c>ExecuteOuter</c> over the
	/// candidate set, materialising each candidate's result slot before <c>Init</c>; a post-walk
	/// <c>RetainNonEmptyManySlots</c> drops lefts whose per-left <see cref="QueryResults{TRightValue}"/>
	/// stayed empty (no rights, or the filter rejected all of them) and narrows candidates to the
	/// survivors.
	/// </summary>
	void IJoinResolver.UnsafeExecuteIndexedInner<TAccessor, TExecutor>(
		ref TAccessor accessor,
		ref TExecutor leftQuery,
		bool cloneOnAdd,
		bool isFirst,
		ref QueryResultsDisposer disposer) {
		ref var candidates = ref leftQuery.GetCandidates<TLeftKey>();
		if (!candidates.IsInitlized || candidates.Count == 0)
			return;

		var inner = new InnerKeyedContainer<TAccessor>(accessor, cloneOnAdd, disposer.IsActive);
		var rounds = new JoinManyRounds<TLeftKey, TRightKey>(candidates.Count);
		try {
			var anyPairs = false;
			foreach (var leftKey in candidates) {
				if (!TryGetBucket(leftKey, out var bucket))
					continue;

				// For inner mode, the slot doesn't pre-exist (no outer base execute ran yet).
				// Materialise it via GetValueRefOrAddDefault BEFORE Init — otherwise Init's
				// GetValueRef call returns NullRef and silently skips _totalCount tracking,
				// leaving PrepareSharedBuffer with a zero-length buffer.
				_ = accessor.GetValueRefOrAddDefault<TLeftKey, QueryResults<TRightValue>>(leftKey, out _);

				var added = RecordPairs(ref rounds, leftKey, bucket);
				if (added > 0) {
					inner.Init(leftKey, added);
					anyPairs = true;
				}
			}

			if (!anyPairs) {
				// No left has any right — Inner semantic narrows to empty.
				candidates.IntersectWith(ReadOnlySpan<TLeftKey>.Empty);
				return;
			}

			inner.PrepareSharedBuffer();
			RegisterPooledBuffer(ref disposer, inner.GetSharedBuffer());

			RunRounds(ref rounds, ref inner);

			// Post-walk: drop slots with QueryResults.Count == 0 (no rights or all filtered out)
			// and narrow candidates to surviving keys. Subsequent base execute fills Left for survivors.
			accessor.RetainNonEmptyManySlots<TLeftKey, TRightValue>(ref candidates);
		}
		finally {
			rounds.Dispose();
		}
	}

	/// <summary>
	/// Keyed inner container used during execution. Same shape as the legacy
	/// ManyResolver's container — Init tracks per-left capacity, PrepareSharedBuffer
	/// allocates the contiguous TRightValue[] partition.
	/// </summary>
	private ref struct InnerKeyedContainer<TAccessor> : IJoinedKeyedResultContainer<TLeftKey, TRightValue>
		where TAccessor : struct, IUnsafeValueAccessor, allows ref struct {
		private readonly bool _cloneOnAdd;
		private readonly bool _shouldPool;
		private int _totalCount;
		private TRightValue[]? _sharedBuffer;
		private TAccessor _accessor;

		public int TotalCount => _totalCount;

		public InnerKeyedContainer(TAccessor accessor, bool cloneOnAdd, bool shouldPool) {
			_accessor = accessor;
			_cloneOnAdd = cloneOnAdd;
			_shouldPool = shouldPool;
			_totalCount = 0;
			_sharedBuffer = null;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Init(TLeftKey foreignKey, int maxCount) {
			ref var valueRef = ref _accessor.GetValueRef<TLeftKey, QueryResults<TRightValue>>(foreignKey);
			if (!Unsafe.IsNullRef(in valueRef)) {
				valueRef.SetPendingCapacity(maxCount);
				_totalCount += maxCount;
			}
		}

		public void Seal(TLeftKey foreignKey, int actualCount) {
		}

		public void PrepareSharedBuffer() {
			_sharedBuffer = _shouldPool ? PragueArrayPool<TRightValue>.Pool.Rent(_totalCount) : new TRightValue[_totalCount];
			var offset = 0;
			var keys = _accessor.GetKeys<TLeftKey>();
			for (var i = 0; i < keys.Length; i++) {
				ref var valueRef = ref _accessor.GetValueRef<TLeftKey, QueryResults<TRightValue>>(keys[i]);
				if (!Unsafe.IsNullRef(in valueRef))
					offset = valueRef.AssignSharedBuffer(_sharedBuffer, offset);
			}
		}

		public TRightValue[]? GetSharedBuffer() => _shouldPool ? _sharedBuffer : null;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int Add(TLeftKey foreignKey, TRightValue result) {
			ref var valueRef = ref _accessor.GetValueRef<TLeftKey, QueryResults<TRightValue>>(foreignKey);
			if (!Unsafe.IsNullRef(in valueRef))
				return valueRef.UnsafeAdd(_cloneOnAdd ? result.Clone() : result);
			return 0;
		}
	}
}
