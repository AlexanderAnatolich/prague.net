namespace Prague.Core.Tests.Join;

using Prague.Core;

// Demonstrates the O(N) materialization problem of the classic sorted+paged pipeline and pins
// the O(K) bound of the (now transparent) bounded path behind the public Execute* terminals.
// The classic side is exercised through the internal cores directly — the public sorted
// terminals route bounded-eligible shapes away from it. Non-pooled execution heap-allocates the
// values buffer (new TValue[expectedCount]), so GetAllocatedBytesForCurrentThread measures the
// materialization directly and deterministically.
[TestFixture]
[NonParallelizable]
public class TopKAllocationCoreTests {
	internal sealed class TaItem : ICacheEquatable<TaItem>, ICacheClonable<TaItem> {
		public int Id { get; init; }
		public int Order { get; init; }
		public bool CacheEquals(TaItem? other) => other is not null && Id == other.Id && Order == other.Order;
		public int CacheGetHashCode() => HashCode.Combine(Id, Order);
		public TaItem Clone() => new() { Id = Id, Order = Order };
	}

	internal sealed class TaByOrderAsc : IComparer<TaItem> {
		public int Compare(TaItem? x, TaItem? y) => (x?.Order ?? 0).CompareTo(y?.Order ?? 0);
	}

	internal sealed class TaRight : ICacheEquatable<TaRight>, ICacheClonable<TaRight> {
		public int Id { get; init; }
		public bool CacheEquals(TaRight? other) => other is not null && Id == other.Id;
		public int CacheGetHashCode() => Id.GetHashCode();
		public TaRight Clone() => new() { Id = Id };
	}

	private const int SmallN = 1_000;
	private const int BigN = 16_000;
	private const int Take = 10;

	private InMemoryDataCache<int, TaItem> _small = null!;
	private InMemoryDataCache<int, TaItem> _big = null!;
	private InMemoryDataCache<int, TaRight> _smallRight = null!;
	private InMemoryDataCache<int, TaRight> _bigRight = null!;

	[OneTimeSetUp]
	public void SetUp() {
		_small = new InMemoryDataCache<int, TaItem>();
		_big = new InMemoryDataCache<int, TaItem>();
		_smallRight = new InMemoryDataCache<int, TaRight>();
		_bigRight = new InMemoryDataCache<int, TaRight>();
		var rng = new Random(7);
		for (var i = 0; i < SmallN; i++) {
			_small.AddOrUpdate(i, new TaItem { Id = i, Order = rng.Next(1_000_000) });
			_smallRight.AddOrUpdate(i, new TaRight { Id = i });
		}

		rng = new Random(7);
		for (var i = 0; i < BigN; i++) {
			_big.AddOrUpdate(i, new TaItem { Id = i, Order = rng.Next(1_000_000) });
			_bigRight.AddOrUpdate(i, new TaRight { Id = i });
		}
	}

	internal readonly struct TaByOrderAscStruct : IComparer<TaItem> {
		public int Compare(TaItem? x, TaItem? y) => (x?.Order ?? 0).CompareTo(y?.Order ?? 0);
	}

	// The bounded plan must not add a single byte to whatever the query shape already costs: the
	// comparison reaches the containers through the resolver / resolver chain as a struct type
	// parameter, so there is no Comparison<T> delegate and no boxed IComparer<T>. Measured against
	// the same query with no sort at all, which is the floor for that shape.
	[Test]
	public void BoundedPooled_AddsNoAllocationOverTheUnsortedQuery() {
		var unsorted = AllocPerOpPrecise(() => {
			using var r = _small.Query().ExecutePooled();
			return r.Count;
		});
		var bounded = AllocPerOpPrecise(() => {
			using var r = _small.Query().SortBounded(_cachedStruct).ExecutePooled(0, Take);
			return r.Count;
		});

		Assert.That(unsorted, Is.Zero, "fixture sanity: a pooled unsorted query allocates nothing");
		Assert.That(bounded, Is.LessThanOrEqualTo(unsorted),
			$"the bounded simple plan must add nothing: {bounded} B/op vs {unsorted} B/op unsorted");
	}

	[Test]
	public void BoundedJoinedPooled_AddsNoAllocationOverTheUnsortedJoin() {
		var unsortedJoin = AllocPerOpPrecise(() => {
			using var r = _small.Query().InnerJoinOne(_smallRight).ExecutePooled();
			return r.Count;
		});
		var boundedJoin = AllocPerOpPrecise(() => {
			using var r = _small.Query().SortBounded(_cachedStruct).InnerJoinOne(_smallRight).ExecutePooled(0, Take);
			return r.Count;
		});

		// The joined pipeline has a pre-existing per-query cost with no sort involved; what this pins
		// is that opting into the bounded plan adds nothing on top of it.
		Assert.That(boundedJoin, Is.LessThanOrEqualTo(unsortedJoin),
			$"bounded joined plan added allocation: {boundedJoin} B/op vs {unsortedJoin} B/op unsorted");
	}

	// A class comparer allocates nothing library-side either — only the caller's own `new`, which is
	// why this pins a cached instance.
	[Test]
	public void BoundedPooled_WithClassComparer_IsAlsoAllocationFree() {
		var bounded = AllocPerOpPrecise(() => {
			using var r = _small.Query().SortBounded(_cachedClass).ExecutePooled(0, Take);
			return r.Count;
		});

		Assert.That(bounded, Is.Zero);
	}

	private static readonly TaByOrderAsc _cachedClass = new();

	private static readonly TaByOrderAscStruct _cachedStruct = new();

	// Same measurement with enough repetitions that a single small one-off inside the window —
	// tiered-JIT bookkeeping, a GC side allocation on a shared runner — amortises to 0 instead of
	// showing up as 1 B/op. The smallest regression these tests guard against is a 24 B box, so the
	// margin is ample.
	private static long AllocPerOpPrecise(Func<int> run) => AllocPerOp(run, 200);

	private static long AllocPerOp(Func<int> run, int iterations = 20) {
		// Warm up JIT and pools, then measure.
		for (var i = 0; i < 5; i++)
			run();

		var sink = 0L;
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < iterations; i++)
			sink += run();

		var delta = GC.GetAllocatedBytesForCurrentThread() - before;
		Assert.That(sink, Is.GreaterThan(0), "queries did not run");
		return delta / iterations;
	}

	// The classic pipeline, reached through the internal cores (the public sorted terminals now
	// route bounded-eligible shapes to the top-K plan).
	private static int ClassicSimple(InMemoryDataCache<int, TaItem> cache, int take) {
		var q = cache.Query().SortBounded(new TaByOrderAsc());
		var results = q.ExecuteCoreSimple(ref q._resolverChain.Resolver, false, false, 0, take);
		return results.Count;
	}

	private static int ClassicJoined(InMemoryDataCache<int, TaItem> cache, InMemoryDataCache<int, TaRight> right, int take) {
		var q = cache.Query().SortBounded(new TaByOrderAsc()).InnerJoinOne(right);
		var results = q.ExecuteCoreJoined<JoinResult<TaItem, TaRight>>(false, false, 0, take);
		return results.Count;
	}

	// ── The problem: the classic pipeline materializes ALL matched rows ──────

	[Test]
	public void Problem_ClassicPipeline_AllocationGrowsWithN_ForFixedTake() {
		var smallAlloc = AllocPerOp(() => ClassicSimple(_small, Take));
		var bigAlloc = AllocPerOp(() => ClassicSimple(_big, Take));

		// 16x the rows => materialization buffer grows ~16x even though take stays 10.
		// Assert a conservative 8x to stay robust across TFMs/pool states.
		Assert.That(bigAlloc, Is.GreaterThan(smallAlloc * 8),
			$"expected O(N) materialization: N={SmallN} -> {smallAlloc} B/op, N={BigN} -> {bigAlloc} B/op");
	}

	[Test]
	public void Problem_ClassicJoinedPipeline_AllocationGrowsWithN_ForFixedTake() {
		var smallAlloc = AllocPerOp(() => ClassicJoined(_small, _smallRight, Take));
		var bigAlloc = AllocPerOp(() => ClassicJoined(_big, _bigRight, Take));

		Assert.That(bigAlloc, Is.GreaterThan(smallAlloc * 8),
			$"expected O(N) joined materialization: N={SmallN} -> {smallAlloc} B/op, N={BigN} -> {bigAlloc} B/op");
	}

	// ── The fix: paged Execute on a sorted chain is bounded by K, not N ──────

	[Test]
	public void PagedExecute_AllocationBoundedByK_NotByN() {
		var smallAlloc = AllocPerOp(() => _small.Query().SortBounded(new TaByOrderAsc()).Execute(0, Take).Count);
		var bigAlloc = AllocPerOp(() => _big.Query().SortBounded(new TaByOrderAsc()).Execute(0, Take).Count);

		// 16x the rows must NOT multiply allocation; allow 2x slack for incidental noise.
		Assert.That(bigAlloc, Is.LessThan(Math.Max(smallAlloc, 512) * 2),
			$"expected O(K) materialization: N={SmallN} -> {smallAlloc} B/op, N={BigN} -> {bigAlloc} B/op");
	}

	[Test]
	public void PagedExecute_AllocatesFarLessThanClassic_OnBigStore() {
		var classic = AllocPerOp(() => ClassicSimple(_big, Take));
		var bounded = AllocPerOp(() => _big.Query().SortBounded(new TaByOrderAsc()).Execute(0, Take).Count);

		Assert.That(bounded, Is.LessThan(classic / 10),
			$"classic={classic} B/op, bounded={bounded} B/op at N={BigN}, take={Take}");
	}

	[Test]
	public void PagedJoinedExecute_AllocationBoundedByK_NotByN() {
		var smallAlloc = AllocPerOp(() =>
			_small.Query().SortBounded(new TaByOrderAsc()).InnerJoinOne(_smallRight).Execute(0, Take).Count);
		var bigAlloc = AllocPerOp(() =>
			_big.Query().SortBounded(new TaByOrderAsc()).InnerJoinOne(_bigRight).Execute(0, Take).Count);

		Assert.That(bigAlloc, Is.LessThan(Math.Max(smallAlloc, 512) * 2),
			$"expected O(K) joined materialization: N={SmallN} -> {smallAlloc} B/op, N={BigN} -> {bigAlloc} B/op");
	}

	[Test]
	public void PagedJoinedExecute_AllocatesFarLessThanClassic_OnBigStore() {
		var classic = AllocPerOp(() => ClassicJoined(_big, _bigRight, Take));
		var bounded = AllocPerOp(() =>
			_big.Query().SortBounded(new TaByOrderAsc()).InnerJoinOne(_bigRight).Execute(0, Take).Count);

		Assert.That(bounded, Is.LessThan(classic / 10),
			$"classic={classic} B/op, bounded={bounded} B/op at N={BigN}, take={Take}");
	}
}
