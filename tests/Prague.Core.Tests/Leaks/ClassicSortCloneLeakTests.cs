namespace Prague.Core.Tests.Leaks;

using Prague.Core;
using Prague.Core.Tests.Infrastructure;
using NUnit.Framework;

// ── Domain model (hand-rolled, no codegen) ────────────────────────────────────

internal sealed class LkItem : ICacheEquatable<LkItem>, ICacheClonable<LkItem> {
	[ThreadStatic] public static bool ThrowOnClone;

	public int Id { get; init; }
	public int Order { get; init; }
	public bool CacheEquals(LkItem? other) => other is not null && other.Id == Id && other.Order == Order;
	public int CacheGetHashCode() => HashCode.Combine(Id, Order);
	public LkItem Clone() => ThrowOnClone ? throw new InvalidOperationException("hostile Clone") : new() { Id = Id, Order = Order };
}

internal sealed class LkRight : ICacheEquatable<LkRight>, ICacheClonable<LkRight> {
	public int Id { get; init; }
	public bool CacheEquals(LkRight? other) => other is not null && other.Id == Id;
	public int CacheGetHashCode() => Id;
	public LkRight Clone() => new() { Id = Id };
}

internal sealed class LkByOrder : IComparer<LkItem> {
	public int Compare(LkItem? x, LkItem? y) => x!.Order.CompareTo(y!.Order);
}

// Compares normally for a while, then throws from inside the sort.
internal sealed class LkHostileComparer : IComparer<LkItem> {
	private int _calls;
	public int Compare(LkItem? x, LkItem? y) => ++_calls == 40 ? throw new InvalidOperationException("hostile comparer") : x!.Order.CompareTo(y!.Order);
}

// The classic pipeline builds its result inside BuildResults: sort, slice, clone. A comparer or a
// Clone() that throws there used to leave the rented buffer — and, for joins, the values array and
// the Many-buffer disposer — already marked as handed off, so nobody returned it. Every scenario must
// come back to zero new outstanding pool arrays and zero double-returns. Caches are built once per
// fixture so only per-query rentals show up in the delta.
[TestFixture]
[NonParallelizable]
public class ClassicSortCloneLeakTests {
	private const int Size = 300;
	private InMemoryDataCache<int, LkItem> _items = null!;
	private InMemoryDataCache<int, LkRight> _right = null!;

	[OneTimeSetUp]
	public void SetUp() {
		_items = new InMemoryDataCache<int, LkItem>();
		_right = new InMemoryDataCache<int, LkRight>();
		for (var i = 0; i < Size; i++) {
			_items.AddOrUpdate(i, new LkItem { Id = i, Order = i * 7919 % Size });
			_right.AddOrUpdate(i, new LkRight { Id = i });
		}
	}

	[TearDown]
	public void TearDown() => LkItem.ThrowOnClone = false;

	// ── Comparer throws ───────────────────────────────────────────────────────

	[Test]
	public void Sorted_ComparerThrows_Unbounded_DoesNotLeak() =>
		LeakAssert.Balanced(() =>
			Assert.Throws<InvalidOperationException>(() => _items.Query().Sort(new LkHostileComparer()).ExecutePooled()));

	// A finite page that holds the whole result runs the same classic sort.
	[Test]
	public void Sorted_ComparerThrows_FullPage_DoesNotLeak() =>
		LeakAssert.Balanced(() =>
			Assert.Throws<InvalidOperationException>(() => _items.Query().Sort(new LkHostileComparer()).ExecutePooled(0, Size)));

	[Test]
	public void SortedInnerJoin_ComparerThrows_Unbounded_DoesNotLeak() =>
		LeakAssert.Balanced(() =>
			Assert.Throws<InvalidOperationException>(() => _items.Query().Sort(new LkHostileComparer()).InnerJoinOne(_right).ExecutePooled()));

	// ── Clone() throws ────────────────────────────────────────────────────────
	//
	// Cloning moves into BuildResults exactly when the result is sliced (a finite page), so the full
	// page is the shape that clones after the sort; unbounded pooled-cloned queries clone in Add.

	[Test]
	public void Sorted_CloneThrows_FullPage_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			LkItem.ThrowOnClone = true;
			try {
				Assert.Throws<InvalidOperationException>(() => _items.Query().Sort(new LkByOrder()).ExecutePooledCloned(0, Size));
			} finally {
				LkItem.ThrowOnClone = false;
			}
		});

	[Test]
	public void Unsorted_CloneThrows_Page_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			LkItem.ThrowOnClone = true;
			try {
				Assert.Throws<InvalidOperationException>(() => _items.Query().ExecutePooledCloned(0, Size / 2));
			} finally {
				LkItem.ThrowOnClone = false;
			}
		});

	[Test]
	public void SortedInnerJoin_CloneThrows_FullPage_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			LkItem.ThrowOnClone = true;
			try {
				Assert.Throws<InvalidOperationException>(() => _items.Query().Sort(new LkByOrder()).InnerJoinOne(_right).ExecutePooledCloned(0, Size));
			} finally {
				LkItem.ThrowOnClone = false;
			}
		});

	[Test]
	public void SortedInnerJoin_CloneThrows_Unbounded_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			LkItem.ThrowOnClone = true;
			try {
				Assert.Throws<InvalidOperationException>(() => _items.Query().Sort(new LkByOrder()).InnerJoinOne(_right).ExecutePooledCloned());
			} finally {
				LkItem.ThrowOnClone = false;
			}
		});

	// The happy path of the same shapes stays balanced too.
	[Test]
	public void Sorted_Cloned_FullPage_HappyPath_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			using var page = _items.Query().Sort(new LkByOrder()).ExecutePooledCloned(0, Size);
			Assert.That(page.Count, Is.EqualTo(Size));
			using var joined = _items.Query().Sort(new LkByOrder()).InnerJoinOne(_right).ExecutePooledCloned(0, Size);
			Assert.That(joined.Count, Is.EqualTo(Size));
		});
}
