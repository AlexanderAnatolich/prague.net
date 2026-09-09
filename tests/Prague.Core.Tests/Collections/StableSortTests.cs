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

	// The keyed overload sorts the same index array as the single-span one; a struct comparer must give
	// the same stable order, and the item span must follow every row.
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

	// Past the depth budget the keyed path falls back to the index heapsort too; it must be just as
	// stable, and the items must still follow.
	[TestCase(33)]
	[TestCase(1000)]
	[TestCase(4097)]
	public void SortKeyed_DepthExhausted_FallsBackToStableHeapSort(int length) {
		var random = new Random(17);
		foreach (var shape in new[] { "random", "few-values", "equal", "descending" }) {
			var rows = Rows(shape, length, random);
			var items = Enumerable.Range(0, length).ToArray();
			var expected = rows.OrderBy(r => r.Key).ToArray();
			StableSort.Sort(rows.AsSpan(), items.AsSpan(), new ByKeyStruct(), depthLimit: 0);
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

	// Span<T>.Sort treats a null comparer as Comparer<T>.Default; so did the classic pipeline before
	// StableSort, and QueryResults.Sort<IComparer<T>>(null) still must. Two rows take the direct
	// insertion sort, 33 the index sort.
	[TestCase(2)]
	[TestCase(33)]
	public void Sort_NullComparer_MeansDefault(int length) {
		var random = new Random(41);
		var values = Enumerable.Range(0, length).Select(_ => random.Next(100)).ToArray();
		var expected = values.OrderBy(v => v).ToArray();

		var plain = (int[])values.Clone();
		StableSort.Sort<int, IComparer<int>>(plain.AsSpan(), null!);
		Assert.That(plain, Is.EqualTo(expected));

		var keyed = (int[])values.Clone();
		var items = Enumerable.Range(0, length).ToArray();
		StableSort.Sort<int, int, IComparer<int>>(keyed.AsSpan(), items.AsSpan(), null!);
		Assert.That(keyed, Is.EqualTo(expected));
		Assert.That(items.Select(i => values[i]), Is.EqualTo(expected), "items must follow their values");

		var results = QueryResults<int>.FromArray((int[])values.Clone(), 0, length, length, false);
		results.Sort<IComparer<int>>(null!);
		Assert.That(results.ToArray(), Is.EqualTo(expected));
	}

	[Test]
	public void SortKeyed_LengthMismatch_Throws() {
		var rows = Rows("random", 10, new Random(1));
		var items = new int[9];
		Assert.Throws<ArgumentException>(() => StableSort.Sort(rows.AsSpan(), items.AsSpan(), new ByKey()));
	}

	[Test]
	public void Sort_PoolBalanced() {
		var rows = Rows("random", 20000, new Random(9));
		LeakAssert.Balanced(() => {
			var sorted = (Row[])rows.Clone();
			StableSort.Sort(sorted.AsSpan(), new ByKey());
		});
	}

	// Issue #77: the comparer reaches every comparison as a type parameter, so a struct comparer costs
	// no box and no Comparison<T> delegate on either overload — the framework's Span.Sort takes 88 B
	// (single span) and 24 B (keyed) for the same struct. Tied keys so the keyed overload also walks
	// its tie-repair path. Averaged over enough runs that a one-off JIT side allocation cannot read as
	// a per-call byte; the smallest regression this guards is a 24 B box.
	[Test]
	public void Sort_StructComparer_AllocatesNothing_OnEitherOverload() {
		var rows = Rows("few-values", 5000, new Random(9));
		var scratch = (Row[])rows.Clone();
		var items = new int[rows.Length];

		Assert.That(AllocPerOp(() => {
			rows.CopyTo(scratch, 0);
			StableSort.Sort(scratch.AsSpan(), new ByKeyStruct());
		}), Is.Zero, "single-span overload");

		Assert.That(AllocPerOp(() => {
			rows.CopyTo(scratch, 0);
			StableSort.Sort(scratch.AsSpan(), items.AsSpan(), new ByKeyStruct());
		}), Is.Zero, "keyed overload");
	}

	// The delegate is the caller's; the sort itself adds nothing per call around it.
	[Test]
	public void Sort_ComparisonDelegate_AllocatesNothingBeyondTheDelegateItself() {
		var rows = Rows("random", 5000, new Random(9));
		var scratch = (Row[])rows.Clone();
		var comparison = new Comparison<Row>(new ByKey().Compare);

		Assert.That(AllocPerOp(() => {
			rows.CopyTo(scratch, 0);
			StableSort.Sort(scratch.AsSpan(), comparison);
		}), Is.Zero);
	}

	private static long AllocPerOp(Action run, int iterations = 50) {
		for (var i = 0; i < 5; i++)
			run();

		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < iterations; i++)
			run();

		return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
	}
}
