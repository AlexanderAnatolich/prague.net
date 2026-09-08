namespace Prague.Core.Tests.Collections;

using System.Linq;
using Prague.Core.Collections;
using Prague.Core.Tests.Infrastructure;
using NUnit.Framework;

// The classic sorted pipeline sorts with StableSort: equal items keep their encounter order, so
// unbounded results, finite pages and page concatenations agree. Reference behaviour is LINQ's
// OrderBy, which is stable by contract.
[TestFixture]
public class StableSortTests {
	private sealed record Row(int Key, int Ordinal);

	private sealed class ByKey : IComparer<Row> {
		public int Compare(Row? x, Row? y) => x!.Key.CompareTo(y!.Key);
	}

	private readonly struct ByKeyStruct : IComparer<Row> {
		public int Compare(Row? x, Row? y) => x!.Key.CompareTo(y!.Key);
	}

	private static Row[] Rows(string shape, int length, Random random) {
		var rows = new Row[length];
		for (var i = 0; i < length; i++) {
			var key = shape switch {
				"ascending" => i / 3,
				"descending" => (length - i) / 3,
				"equal" => 1,
				"organ-pipe" => Math.Min(i, length - i) / 2,
				"few-values" => random.Next(3),
				_ => random.Next(64)
			};
			rows[i] = new Row(key, i);
		}

		return rows;
	}

	[TestCase(0)]
	[TestCase(1)]
	[TestCase(2)]
	[TestCase(31)]
	[TestCase(32)]
	[TestCase(33)]
	[TestCase(64)]
	[TestCase(65)]
	[TestCase(100)]
	[TestCase(1000)]
	[TestCase(4097)]
	[TestCase(70000)]
	public void Sort_MatchesStableReference_ForEveryShape(int length) {
		var random = new Random(17);
		foreach (var shape in new[] { "random", "ascending", "descending", "equal", "organ-pipe", "few-values" }) {
			var rows = Rows(shape, length, random);
			var expected = rows.OrderBy(r => r.Key).ToArray();

			var sorted = (Row[])rows.Clone();
			StableSort.Sort(sorted.AsSpan(), new ByKey());

			Assert.That(sorted, Is.EqualTo(expected), $"{shape} n={length}: equal keys must keep encounter order");
		}
	}

	[Test]
	public void Sort_StructComparer_IsStableToo() {
		var random = new Random(5);
		var rows = Rows("few-values", 5000, random);
		var expected = rows.OrderBy(r => r.Key).ToArray();

		StableSort.Sort(rows.AsSpan(), new ByKeyStruct());

		Assert.That(rows, Is.EqualTo(expected));
	}

	[Test]
	public void Sort_RespectsTheSlice() {
		var rows = Rows("random", 300, new Random(3));
		var data = new Row[302];
		data[0] = new Row(int.MinValue, -1);
		data[^1] = new Row(int.MaxValue, -2);
		rows.CopyTo(data, 1);

		StableSort.Sort(data.AsSpan(1, 300), new ByKey());

		Assert.That(data[0], Is.EqualTo(new Row(int.MinValue, -1)));
		Assert.That(data[^1], Is.EqualTo(new Row(int.MaxValue, -2)));
		Assert.That(data.Skip(1).Take(300), Is.EqualTo(rows.OrderBy(r => r.Key)));
	}

	[TestCase(2)]
	[TestCase(33)]
	[TestCase(1000)]
	[TestCase(70000)]
	public void SortKeyed_MovesItemsAlongside_AndStaysStable(int length) {
		var random = new Random(23);
		foreach (var shape in new[] { "random", "few-values", "equal", "descending" }) {
			var rows = Rows(shape, length, random);
			var items = Enumerable.Range(0, length).ToArray();
			var expected = rows.OrderBy(r => r.Key).ToArray();

			var sorted = (Row[])rows.Clone();
			StableSort.Sort(sorted.AsSpan(), items.AsSpan(), new ByKey());

			Assert.That(sorted, Is.EqualTo(expected), shape);
			for (var i = 0; i < length; i++) {
				Assert.That(items[i], Is.EqualTo(sorted[i].Ordinal), $"{shape}: items[{i}] must follow its value");
			}
		}
	}

	[TestCase(33)]
	[TestCase(1000)]
	[TestCase(4097)]
	public void Sort_DepthExhausted_FallsBackToStableHeapSort(int length) {
		var random = new Random(11);
		var comparison = new Comparison<Row>(new ByKey().Compare);
		foreach (var shape in new[] { "random", "few-values", "equal", "descending" }) {
			var rows = Rows(shape, length, random);
			var expected = rows.OrderBy(r => r.Key).ToArray();
			var sorted = (Row[])rows.Clone();
			StableSort.Sort(sorted.AsSpan(), comparison, depthLimit: 0);
			Assert.That(sorted, Is.EqualTo(expected), shape);
		}
	}

	// The keyed overload sorts in place with the framework sort and repairs the ties afterwards; a
	// struct comparer must give the same stable order, and the item span must follow every row.
	[TestCase(33)]
	[TestCase(5000)]
	public void SortKeyed_StructComparer_IsStableToo(int length) {
		var random = new Random(29);
		foreach (var shape in new[] { "random", "few-values", "equal", "organ-pipe" }) {
			var rows = Rows(shape, length, random);
			var items = Enumerable.Range(0, length).ToArray();
			var expected = rows.OrderBy(r => r.Key).ToArray();
			StableSort.Sort(rows.AsSpan(), items.AsSpan(), new ByKeyStruct());
			Assert.That(rows, Is.EqualTo(expected), shape);
			for (var i = 0; i < length; i++) {
				Assert.That(items[i], Is.EqualTo(rows[i].Ordinal), $"{shape}: items[{i}] must follow its value");
			}
		}
	}

	[Test]
	public void SortKeyed_PoolBalanced() {
		var rows = Rows("few-values", 20000, new Random(13));
		LeakAssert.Balanced(() => {
			var sorted = (Row[])rows.Clone();
			var items = Enumerable.Range(0, rows.Length).ToArray();
			StableSort.Sort(sorted.AsSpan(), items.AsSpan(), new ByKey());
		});
	}

	[Test]
	public void SortKeyed_LengthMismatch_Throws() {
		var rows = Rows("random", 10, new Random(1));
		var items = new int[9];
		Assert.Throws<ArgumentException>(() => StableSort.Sort(rows.AsSpan(), items.AsSpan(), new ByKey()));
	}

	[Test]
	public void Sort_PoolBalanced_AndNoGcAllocationBeyondTheComparisonDelegate() {
		var rows = Rows("random", 20000, new Random(9));
		LeakAssert.Balanced(() => {
			var sorted = (Row[])rows.Clone();
			StableSort.Sort(sorted.AsSpan(), new ByKey());
		});

		var comparison = new Comparison<Row>(new ByKey().Compare);
		var scratch = (Row[])rows.Clone();
		StableSort.Sort(scratch.AsSpan(), comparison);
		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < 5; i++) {
			rows.CopyTo(scratch, 0);
			StableSort.Sort(scratch.AsSpan(), comparison);
		}

		Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.LessThan(1024), "the merge buffers must come from the pool");
	}
}
