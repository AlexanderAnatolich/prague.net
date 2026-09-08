namespace Prague.Core.Tests.Collections;

using Prague.Core.Collections;

// Bounded top-K selection primitive: Push keeps the K smallest items per comparer
// (max-heap of kept items, root = current worst); DrainAscending heapsorts in place.
[TestFixture]
public class TopKSelectTests {
	[TestCase(0)]
	[TestCase(1)]
	[TestCase(16)]
	[TestCase(17)]
	[TestCase(1000)]
	[TestCase(10000)]
	public void LargePrefixSort_MatchesFrameworkSort_AndRespectsSlice(int length) {
		var random = new Random(73);
		var comparer = Comparer<int>.Create(static (a, b) => b.CompareTo(a));
		foreach (var shape in new[] { "random", "ascending", "descending", "equal", "organ-pipe" }) {
			var data = new int[length + 2];
			data[0] = int.MinValue;
			data[^1] = int.MaxValue;
			for (var i = 0; i < length; i++)
				data[i + 1] = shape switch {
					"ascending" => i,
					"descending" => length - i,
					"equal" => 1,
					"organ-pipe" => Math.Min(i, length - i),
					_ => random.Next(37)
				};
			var expected = (int[])data.Clone();
			Array.Sort(expected, 1, length, comparer);
			TopKSelect.SortAscending(data.AsSpan(1, length), comparer);
			Assert.That(data, Is.EqualTo(expected), shape);
		}
	}

	private sealed class IntAsc : IComparer<int> {
		public int Compare(int x, int y) => x.CompareTo(y);
	}

	private static int[] Select(IEnumerable<int> source, int k) {
		var buffer = new int[Math.Max(k, 1)];
		var count = 0;
		var heapified = false;
		var cmp = new IntAsc();
		foreach (var item in source)
			TopKSelect.Push(buffer, ref count, ref heapified, k, item, cmp);

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
		foreach (var item in new[] { 5, 9, 1, 7, 3 })
			TopKSelect.Push(buffer, ref count, ref heapified, 3, item, cmp);

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
		foreach (var item in new[] { 4, 2, 9, 1 })
			TopKSelect.Push(buffer, ref count, ref heapified, 3, item, cmp);

		var n = TopKSelect.DrainAscending(buffer, ref count, ref heapified, cmp);
		Assert.That(buffer.AsSpan(0, n).ToArray(), Is.EqualTo(new[] { 1, 2, 4 }));
	}

	// ── Large prefix: introselect the page bounds, sort only the page ─────────

	private static int[] Shape(string shape, int length, Random random) {
		var data = new int[length];
		for (var i = 0; i < length; i++) {
			data[i] = shape switch {
				"ascending" => i,
				"descending" => length - i,
				"equal" => 1,
				"organ-pipe" => Math.Min(i, length - i),
				"few-values" => random.Next(3),
				_ => random.Next(1 << 20)
			};
		}

		return data;
	}

	// Page shapes per length: first pages, middle pages, the last rows, pages past the end, empty pages.
	private static IEnumerable<(int Skip, int Take)> Pages(int n) {
		yield return (0, 1);
		yield return (0, 3);
		yield return (0, n);
		yield return (0, n + 5);
		yield return (1, 2);
		yield return (n / 2, 3);
		yield return (n / 4, n / 2);
		yield return (Math.Max(n - 3, 0), 3);
		yield return (Math.Max(n - 1, 0), 10);
		yield return (n, 5);
		yield return (n + 7, 5);
		yield return (2, 0);
		yield return (0, 0);
		yield return (-1, 5);
	}

	[TestCase(0, false)]
	[TestCase(1, false)]
	[TestCase(2, false)]
	[TestCase(16, false)]
	[TestCase(17, false)]
	[TestCase(33, false)]
	[TestCase(1000, false)]
	[TestCase(4097, false)]
	[TestCase(17, true)]
	[TestCase(1000, true)]
	[TestCase(4097, true)]
	public void SelectPage_MatchesSortedSlice_ForEveryShapeAndPage(int length, bool forceFallbacks) {
		var random = new Random(91);
		var comparer = new IntAsc();
		foreach (var shape in new[] { "random", "ascending", "descending", "equal", "organ-pipe", "few-values" }) {
			var source = Shape(shape, length, random);
			var sorted = (int[])source.Clone();
			Array.Sort(sorted);
			foreach (var (skip, take) in Pages(length)) {
				// Sentinels outside the span pin the slice; the multiset inside must survive unchanged.
				var data = new int[length + 2];
				data[0] = int.MinValue;
				data[^1] = int.MaxValue;
				source.CopyTo(data, 1);
				var span = data.AsSpan(1, length);

				var page = forceFallbacks
					? TopKSelect.SelectPage(span, skip, take, comparer, depthLimit: 0)
					: TopKSelect.SelectPage(span, skip, take, comparer);

				var label = $"{shape} n={length} skip={skip} take={take}";
				var expectedPage = skip < 0 || take <= 0 || skip >= length ? 0 : Math.Min(take, length - skip);
				Assert.That(page, Is.EqualTo(expectedPage), label);
				if (page > 0)
					Assert.That(data.AsSpan(1 + skip, page).ToArray(), Is.EqualTo(sorted.AsSpan(skip, page).ToArray()), label);

				Assert.That(data[0], Is.EqualTo(int.MinValue), label);
				Assert.That(data[^1], Is.EqualTo(int.MaxValue), label);
				var remaining = data.AsSpan(1, length).ToArray();
				Array.Sort(remaining);
				Assert.That(remaining, Is.EqualTo(sorted), label);
			}
		}
	}

	[TestCase(17)]
	[TestCase(1000)]
	public void LargePrefixSort_DepthExhausted_FallsBackToHeapSort(int length) {
		var random = new Random(5);
		var comparer = new IntAsc();
		foreach (var shape in new[] { "random", "descending", "equal", "few-values" }) {
			var data = Shape(shape, length, random);
			var expected = (int[])data.Clone();
			Array.Sort(expected);
			TopKSelect.SortAscending(data.AsSpan(), comparer, depthLimit: 0);
			Assert.That(data, Is.EqualTo(expected), shape);
		}
	}
}
