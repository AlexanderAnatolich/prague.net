namespace Prague.Core.Tests.Join;

using Prague.Core.Tests.Infrastructure;
using static TopKExecuteCoreTests;

[TestFixture]
[NonParallelizable]
public class TopKRegressionTests {
	[TestCase(false, 2)]
	[TestCase(false, 17)]
	[TestCase(false, 32)]
	[TestCase(true, 2)]
	[TestCase(true, 17)]
	[TestCase(true, 32)]
	public void EqualSortKeys_PagesCoverEachRowOnce_AcrossSelectionPlans(bool joined, int pageSize) {
		var left = CreateCache();
		var right = CreateCache();
		var comparer = Comparer<TkItem>.Create(static (_, _) => 0);
		var actual = new List<int>();
		int[] expected;
		if (joined) {
			using var full = left.Query().SortBounded(comparer).InnerJoinOne(right).ExecutePooled(0, 96);
			expected = full.Select(x => x.Left.Id).ToArray();
		} else {
			using var full = left.Query().SortBounded(comparer).ExecutePooled(0, 96);
			expected = full.Select(x => x.Id).ToArray();
		}
		for (var skip = 0; skip < 96; skip += pageSize) {
			if (joined) {
				using var page = left.Query().SortBounded(comparer).InnerJoinOne(right).ExecutePooled(skip, pageSize);
				actual.AddRange(page.Select(x => x.Left.Id));
				Assert.That(page.Count, Is.EqualTo(Math.Min(pageSize, 96 - skip)));
			} else {
				using var page = left.Query().SortBounded(comparer).ExecutePooled(skip, pageSize);
				actual.AddRange(page.Select(x => x.Id));
				Assert.That(page.Count, Is.EqualTo(Math.Min(pageSize, 96 - skip)));
			}
		}
		Assert.That(actual, Is.EqualTo(expected));
		Assert.That(actual.Distinct().Count(), Is.EqualTo(96));
	}

	[TestCase(false, false)]
	[TestCase(true, false)]
	[TestCase(false, true)]
	[TestCase(true, true)]
	public void InnerJoin_KeepsValidatedValues_WhenCacheChangesBeforeFill(bool remove, bool clone) {
		var left = CreateCache();
		var right = CreateCache();
		var changed = false;
		var calls = 0;
		var comparer = Comparer<TkItem>.Create((a, b) => {
			if (!changed) {
				changed = true;
				for (var i = 0; i < 96; i++) {
					if (remove)
						right.Remove(i);
					else
						right.AddOrUpdate(i, new TkItem { Id = i, Order = -1 });
				}
			}
			return a.Id.CompareTo(b.Id);
		});
		var query = left.Query().SortBounded(comparer).InnerJoinOne(right, q => q.Where(x => {
			calls++;
			return x.Order == 1;
		}));
		using var result = clone ? query.ExecutePooledCloned(0, 5) : query.ExecutePooled(0, 5);
		Assert.That(changed, Is.True);
		Assert.That(calls, Is.EqualTo(96));
		Assert.That(result.Count, Is.EqualTo(5));
		Assert.That(result.TotalCount, Is.EqualTo(96));
		Assert.That(result.Select(x => x.Right!.Order), Is.All.EqualTo(1));
	}

	[Test]
	public void LaterInnerFilterThrows_EarlierRetainedValuesAreReturned() {
		var left = CreateCache();
		var right = CreateCache();
		LeakAssert.Balanced(() => {
			Assert.Throws<InvalidOperationException>(() => {
				using var result = left.Query().SortBounded(new TkByOrderAsc())
					.InnerJoinOne(right)
					.InnerJoinOne(right, q => q.Where(static _ => throw new InvalidOperationException("filter")))
					.ExecutePooled(0, 5);
			});
		});
	}

	[Test]
	public void FullSort_ThrowingComparer_ReturnsSelectionAndRetainedBuffers() {
		var left = CreateCache();
		var right = CreateCache();
		var comparer = Comparer<TkItem>.Create(static (_, _) => throw new InvalidOperationException("sort"));
		LeakAssert.Balanced(() => {
			Assert.Throws<InvalidOperationException>(() => {
				using var result = left.Query().SortBounded(comparer).InnerJoinOne(right).ExecutePooled(0, 96);
			});
		});
	}

	private static InMemoryDataCache<int, TkItem> CreateCache() {
		var cache = new InMemoryDataCache<int, TkItem>();
		for (var i = 0; i < 96; i++)
			cache.AddOrUpdate(i, new TkItem { Id = i, Order = 1 });
		return cache;
	}
}
