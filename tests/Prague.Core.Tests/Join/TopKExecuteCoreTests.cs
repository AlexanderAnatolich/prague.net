namespace Prague.Core.Tests.Join;

using Prague.Core;

// Top-K bounded execution: comparer seam (Task 2), simple-path terminals (Task 4),
// joined-path terminals (Task 8).
[TestFixture]
public class TopKExecuteCoreTests {
	internal sealed class TkItem : ICacheEquatable<TkItem>, ICacheClonable<TkItem> {
		public int Id { get; init; }
		public int Order { get; init; }
		public bool CacheEquals(TkItem? other) => other is not null && Id == other.Id && Order == other.Order;
		public int CacheGetHashCode() => HashCode.Combine(Id, Order);
		public TkItem Clone() => new() { Id = Id, Order = Order };
	}

	internal sealed class TkByOrderAsc : IComparer<TkItem> {
		public int Compare(TkItem? x, TkItem? y) => (x?.Order ?? 0).CompareTo(y?.Order ?? 0);
	}

	internal sealed class TkJoinedComparer : IComparer<JoinResult<TkItem, TkItem>> {
		public int Compare(JoinResult<TkItem, TkItem> x, JoinResult<TkItem, TkItem> y)
			=> x.Left.Order.CompareTo(y.Left.Order);
	}

	// Constrained call helpers: IJoinResolver has static abstract members, so it cannot be used as a
	// variable type — dispatch through the struct constraint instead.
	private static bool OrdersByLeftValues<TResolver, TLeft>(ref TResolver resolver)
		where TResolver : struct, IJoinResolver
		=> resolver.OrdersByLeftValues<TLeft>();

	private static int CompareLeftValues<TResolver, TLeft>(ref TResolver resolver, TLeft a, TLeft b)
		where TResolver : struct, IJoinResolver
		=> resolver.CompareLeftValues(a, b);

	// ── Comparison seam ──
	// The bounded plan never takes the comparer out of the resolver: erasing it to IComparer<TLeft>
	// would box a struct comparer, so the resolver answers "can I order left values?" and then does
	// the comparing itself.

	[Test]
	public void SortResolver_OrdersByLeftValues_AndCompares() {
		var resolver = new SortResolver<int, TkItem, TkItem, TkByOrderAsc>(new TkByOrderAsc());
		Assert.That(OrdersByLeftValues<SortResolver<int, TkItem, TkItem, TkByOrderAsc>, TkItem>(ref resolver), Is.True);

		var a = new TkItem { Id = 1, Order = 1 };
		var b = new TkItem { Id = 2, Order = 2 };
		Assert.That(CompareLeftValues(ref resolver, a, b), Is.LessThan(0));
		Assert.That(CompareLeftValues(ref resolver, b, a), Is.GreaterThan(0));
		Assert.That(CompareLeftValues(ref resolver, a, a), Is.Zero);
	}

	[Test]
	public void SortResolver_JoinResultComparer_DoesNotOrderLeftValues() {
		// TResult != TLeftValue (joined-row comparer) — the bounded plan must refuse it.
		var resolver = new SortResolver<int, TkItem, JoinResult<TkItem, TkItem>, TkJoinedComparer>(new TkJoinedComparer());
		Assert.That(
			OrdersByLeftValues<SortResolver<int, TkItem, JoinResult<TkItem, TkItem>, TkJoinedComparer>, TkItem>(ref resolver),
			Is.False);
	}

	[Test]
	public void BaseResolver_DefaultSeam_DoesNotOrderLeftValues() {
		var resolver = new BaseResolver<int, TkItem>();
		Assert.That(OrdersByLeftValues<BaseResolver<int, TkItem>, TkItem>(ref resolver), Is.False);
	}

	[Test]
	public void Sort_WithoutOptIn_DoesNotAllowBounding() {
		var classic = new SortResolver<int, TkItem, TkItem, TkByOrderAsc>(new TkByOrderAsc());
		var bounded = new SortResolver<int, TkItem, TkItem, TkByOrderAsc>(new TkByOrderAsc(), allowBounded: true);
		Assert.That(classic.AllowsBounded, Is.False);
		Assert.That(bounded.AllowsBounded, Is.True);
	}

	// ── Simple path: paged Execute* (now transparently bounded) vs the full classic order ──
	// The oracle is the unbounded call: take == int.MaxValue always runs the classic full-sort
	// core, so slicing its result reproduces exactly what the classic paged pipeline returned.

	private InMemoryDataCache<int, TkItem> _cache = null!;

	[OneTimeSetUp]
	public void SetUpCache() {
		_cache = new InMemoryDataCache<int, TkItem>();
		var rng = new Random(11);
		for (var i = 0; i < 500; i++) {
			// Unique Order values => tie-free, row-identity comparable across paths.
			_cache.AddOrUpdate(i, new TkItem { Id = i, Order = i * 2 + (rng.Next(2) == 0 ? 0 : 1) });
		}
	}

	[TestCase(0, 10)]
	[TestCase(5, 10)]
	[TestCase(50, 20)]    // heap plan: K = 70 stays below N / 4
	[TestCase(100, 50)]   // collect plan: K = 150 reaches N / 4, page selected in place
	[TestCase(490, 20)]   // page crossing the end: take clamps
	[TestCase(499, 10)]   // page starting at the last row
	[TestCase(500, 10)]   // skip == total
	[TestCase(600, 10)]   // skip past total
	[TestCase(0, 0)]      // empty page
	public void PagedExecutePooled_MatchesFullClassicOrder(int skip, int take) {
		using var full = _cache.Query().SortBounded(new TkByOrderAsc()).ExecutePooled();
		var expected = full.Select(r => r.Id).Skip(skip).Take(take).ToArray();

		using var paged = _cache.Query().SortBounded(new TkByOrderAsc()).ExecutePooled(skip, take);

		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		Assert.That(paged.Count, Is.EqualTo(expected.Length));
		Assert.That(paged.Select(r => r.Id).ToArray(), Is.EqualTo(expected));
	}

	[Test]
	public void PagedExecute_WithWhere_TotalCountIsFilteredTotal() {
		using var paged = _cache.Query().Where(static v => v.Id < 100).SortBounded(new TkByOrderAsc()).Execute(0, 10);
		Assert.That(paged.TotalCount, Is.EqualTo(100));
		Assert.That(paged.Count, Is.EqualTo(10));
	}

	[Test]
	public void UnboundedTake_ReturnsEverything() {
		using var full = _cache.Query().SortBounded(new TkByOrderAsc()).Execute(0, int.MaxValue);
		Assert.That(full.Count, Is.EqualTo(500));
		Assert.That(full.TotalCount, Is.EqualTo(500));
	}

	[Test]
	public void PagedExecutePooledCloned_RowsAreIndependentClones() {
		using var cloned = _cache.Query().SortBounded(new TkByOrderAsc()).ExecutePooledCloned(0, 3);
		var cached = _cache.Query().SortBounded(new TkByOrderAsc()).Execute(0, 3);
		for (var i = 0; i < 3; i++) {
			Assert.That(ReferenceEquals(cloned[i], cached[i]), Is.False, "cloned row must not alias the cache instance");
			Assert.That(cloned[i].CacheEquals(cached[i]), Is.True);
		}
	}
}
