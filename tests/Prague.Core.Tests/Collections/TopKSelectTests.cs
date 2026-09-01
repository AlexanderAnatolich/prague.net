namespace Prague.Core.Tests.Collections;

using Prague.Core.Collections;

// Bounded top-K selection primitive: Push keeps the K smallest items per comparer
// (max-heap of kept items, root = current worst); DrainAscending heapsorts in place.
[TestFixture]
public class TopKSelectTests {
	private sealed class IntAsc : IComparer<int> {
		public int Compare(int x, int y) => x.CompareTo(y);
	}

	private static int[] Select(IEnumerable<int> source, int k) {
		var buffer = new int[Math.Max(k, 1)];
		var count = 0;
		var heapified = false;
		var cmp = new IntAsc();
		foreach (var item in source) {
			TopKSelect.Push(buffer, ref count, ref heapified, k, item, cmp);
		}

		var n = TopKSelect.DrainAscending(buffer, ref count, ref heapified, cmp);
		return buffer.AsSpan(0, n).ToArray();
	}

	[Test]
	public void KeepsSmallestK_Ascending() =>
		Assert.That(Select(new[] { 9, 1, 8, 2, 7, 3, 6, 4, 5 }, 3), Is.EqualTo(new[] { 1, 2, 3 }));

	[Test]
	public void FewerThanK_ReturnsAllSorted() =>
		Assert.That(Select(new[] { 3, 1, 2 }, 10), Is.EqualTo(new[] { 1, 2, 3 }));

	[Test]
	public void ExactlyK_ReturnsAllSorted() =>
		Assert.That(Select(new[] { 3, 1, 2 }, 3), Is.EqualTo(new[] { 1, 2, 3 }));

	[Test]
	public void ZeroK_KeepsNothing() =>
		Assert.That(Select(new[] { 1, 2, 3 }, 0), Is.Empty);

	[Test]
	public void Empty_ReturnsEmpty() =>
		Assert.That(Select(Array.Empty<int>(), 5), Is.Empty);

	[Test]
	public void DescendingComparer_KeepsLargest() {
		var buffer = new int[3];
		var count = 0;
		var heapified = false;
		var cmp = Comparer<int>.Create(static (x, y) => y.CompareTo(x));
		foreach (var item in new[] { 5, 9, 1, 7, 3 }) {
			TopKSelect.Push(buffer, ref count, ref heapified, 3, item, cmp);
		}

		var n = TopKSelect.DrainAscending(buffer, ref count, ref heapified, cmp);
		Assert.That(buffer.AsSpan(0, n).ToArray(), Is.EqualTo(new[] { 9, 7, 5 }));
	}

	[Test]
	public void ManyItems_MatchesFullSort() {
		var rng = new Random(42);
		var data = Enumerable.Range(0, 10_000).Select(_ => rng.Next(100_000)).ToArray();
		var expected = data.OrderBy(x => x).Take(37).ToArray();
		Assert.That(Select(data, 37), Is.EqualTo(expected));
	}

	[Test]
	public void Ties_AtBoundary_FirstSeenWins_SetEquivalent() {
		// All equal: any 2 survive; drain yields 2 items equal to 5.
		Assert.That(Select(new[] { 5, 5, 5, 5 }, 2), Is.EqualTo(new[] { 5, 5 }));
	}

	[Test]
	public void RentedOversizedBuffer_UsesOnlyK() {
		// Buffer longer than k (ArrayPool rents round up) — logic must bound by k, not length.
		var buffer = new int[16];
		var count = 0;
		var heapified = false;
		var cmp = new IntAsc();
		foreach (var item in new[] { 4, 2, 9, 1 }) {
			TopKSelect.Push(buffer, ref count, ref heapified, 3, item, cmp);
		}

		var n = TopKSelect.DrainAscending(buffer, ref count, ref heapified, cmp);
		Assert.That(buffer.AsSpan(0, n).ToArray(), Is.EqualTo(new[] { 1, 2, 4 }));
	}
}
