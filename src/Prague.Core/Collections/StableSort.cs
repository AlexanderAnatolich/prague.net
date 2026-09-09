namespace Prague.Core.Collections;

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
///   Stable ascending sort for the classic (unbounded) sorted pipeline: rows that compare equal keep
///   their encounter order, so a full result, a finite page over the same rows and the concatenation
///   of consecutive pages agree row for row without carrying an ordinal per row.
///   <para>
///   The single-span overload sorts a pooled array of row indices rather than the rows: a three-way
///   introsort partitions the indices by the row key alone, the block of indices whose rows equal the
///   pivot is finished by a plain integer sort (index order is encounter order, so that block is
///   already stable and costs no user comparison), and the two outer blocks recurse. One final pass
///   permutes the rows into place through one pooled buffer. Moving 4-byte indices instead of
///   references keeps the partition loops free of GC write barriers, which is what lets this match the
///   framework's unstable introsort on distinct keys and beat it on heavy ties.
///   </para>
///   <para>
///   The keyed overload — the joined pipeline's row dictionary, arrays of result structs several
///   megabytes long — sorts the values IN PLACE with a specialisation of the framework's keyed
///   introsort, moving a pooled ordinal per row as the sort's items, then restores encounter order
///   inside every run of equal values by sorting the runs' ordinals — integers only — and gathering the
///   rows into that order once; the caller's items follow the ordinals through one more permutation
///   pass. Sorting indices there cost half again the framework sort: every comparison took an extra
///   random hop into the struct rows, which the in-place sort reads sequentially.
///   </para>
///   <para>
///   The comparer reaches every comparison as a type parameter, passed by reference through each
///   frame: a struct comparer is a direct, inlinable call per instantiation and nothing is allocated per
///   sort. Neither a <see cref="Comparison{T}"/> (which the framework's single-span sort builds, boxing a
///   struct comparer on the way — 88 B per query) nor the framework's keyed
///   <c>Span&lt;T&gt;.Sort(keys, items, comparer)</c> (which takes an <c>IComparer&lt;TKey&gt;</c> and so
///   boxes a struct — 24 B) is used. A class comparer dispatches through the interface, as the framework
///   sort does.
///   </para>
///   A comparison that throws leaves the rows in an unspecified permutation like the framework sort
///   (the single-span overload leaves them untouched unless it throws inside the final permutation).
/// </summary>
internal static class StableSort {
	// Below this many rows the index machinery does not pay for itself: a stable insertion sort on the
	// rows themselves finishes the job.
	private const int DirectInsertionThreshold = 32;

	// Ranges this short are insertion-sorted instead of partitioned (the framework's threshold too).
	private const int InsertionSortThreshold = 16;

	/// <summary>
	///   Sorts <paramref name="values"/> ascending; equal items keep their relative order. A null
	///   <paramref name="comparer"/> means <see cref="Comparer{T}.Default"/>, as it does for
	///   <c>Span&lt;T&gt;.Sort</c>.
	/// </summary>
	internal static void Sort<T, TComparer>(Span<T> values, TComparer comparer)
		where TComparer : IComparer<T> {
		if (comparer is null) {
			Sort(values, Comparer<T>.Default);
			return;
		}

		Sort(values, ref comparer, DepthLimit(values.Length));
	}

	/// <summary>
	///   Sorts <paramref name="values"/> ascending and moves <paramref name="items"/> alongside, so
	///   <c>items[i]</c> still belongs to <c>values[i]</c> afterwards; equal values keep their order. A
	///   null <paramref name="comparer"/> means <see cref="Comparer{T}.Default"/>.
	/// </summary>
	internal static void Sort<T, TItem, TComparer>(Span<T> values, Span<TItem> items, TComparer comparer)
		where TComparer : IComparer<T> {
		if (values.Length != items.Length)
			throw new ArgumentException("values and items must have the same length", nameof(items));

		var n = values.Length;
		if (n < 2)
			return;

		if (comparer is null) {
			Sort(values, items, Comparer<T>.Default);
			return;
		}

		if (n <= DirectInsertionThreshold) {
			InsertionSortRows(values, items, ref comparer);
			return;
		}

		var ordinals = PragueArrayPool<int>.Pool.Rent(n);
		var itemBuffer = PragueArrayPool<TItem>.Pool.Rent(n);
		try {
			var order = ordinals.AsSpan(0, n);
			for (var i = 0; i < n; i++)
				order[i] = i;

			// In place, unstable: the ordinals ride along as the sort's items and record where each row
			// came from.
			IntroSortRows(values, order, ref comparer, DepthLimit(n));
			RestoreEncounterOrder(values, order, ref comparer);
			Permute(items, order, itemBuffer.AsSpan(0, n));
		} finally {
			PragueArrayPool<int>.Pool.Return(ordinals);
			PragueArrayPool<TItem>.Pool.Return(itemBuffer, RuntimeHelpers.IsReferenceOrContainsReferences<TItem>());
		}
	}

	internal static void Sort<T>(Span<T> values, Comparison<T> comparison)
		=> Sort(values, comparison, DepthLimit(values.Length));

	/// <summary>Test seam: <paramref name="depthLimit"/> bounds the partitioning depth (0 forces the heapsort fallback).</summary>
	internal static void Sort<T>(Span<T> values, Comparison<T> comparison, int depthLimit) {
		ArgumentNullException.ThrowIfNull(comparison);
		var comparer = new ComparisonComparer<T>(comparison);
		Sort(values, ref comparer, depthLimit);
	}

	private static void Sort<T, TComparer>(Span<T> values, ref TComparer comparer, int depthLimit)
		where TComparer : IComparer<T> {
		var n = values.Length;
		if (n < 2)
			return;

		if (n <= DirectInsertionThreshold) {
			InsertionSortRows(values, ref comparer);
			return;
		}

		var indices = PragueArrayPool<int>.Pool.Rent(n);
		var buffer = PragueArrayPool<T>.Pool.Rent(n);
		try {
			var order = indices.AsSpan(0, n);
			SortIndices(order, values, ref comparer, depthLimit);
			Permute(values, order, buffer.AsSpan(0, n));
		} finally {
			PragueArrayPool<int>.Pool.Return(indices);
			PragueArrayPool<T>.Pool.Return(buffer, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
		}
	}

	// After the unstable in-place sort every run of values the comparer calls equal is contiguous, and
	// order[i] is the encounter position of the row now at i. Within a run the stable order is ascending
	// encounter position: sort each run's positions — integers, no user comparisons, no row moves — and,
	// once any run longer than one row exists, gather the rows into the corrected order in one pass.
	// Distinct keys make every run one row long and cost only the adjacent comparison that finds them.
	private static void RestoreEncounterOrder<T, TComparer>(Span<T> values, Span<int> order, ref TComparer comparer)
		where TComparer : IComparer<T> {
		var n = values.Length;
		int[]? unstable = null; // unstable[encounter position] = index the row holds after the in-place sort
		try {
			var runStart = 0;
			for (var i = 1; i <= n; i++) {
				if (i < n && comparer.Compare(values[i - 1], values[i]) == 0)
					continue;

				var length = i - runStart;
				if (length > 1) {
					if (unstable is null) {
						// First tie: remember where every row sits before any run is re-ordered.
						unstable = PragueArrayPool<int>.Pool.Rent(n);
						for (var k = 0; k < n; k++)
							unstable[order[k]] = k;
					}

					order.Slice(runStart, length).Sort();
				}

				runStart = i;
			}

			if (unstable is null)
				return;

			var buffer = PragueArrayPool<T>.Pool.Rent(n);
			try {
				var gathered = buffer.AsSpan(0, n);
				for (var k = 0; k < n; k++)
					gathered[k] = values[unstable[order[k]]];

				gathered.CopyTo(values);
			} finally {
				PragueArrayPool<T>.Pool.Return(buffer, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
			}
		} finally {
			if (unstable is not null)
				PragueArrayPool<int>.Pool.Return(unstable);
		}
	}

	// Same budget as the framework introsort: past it, partitioning is assumed adversarial.
	private static int DepthLimit(int length) => 2 * (BitOperations.Log2((uint)length) + 1);

	// Adapts the Comparison<T> overloads to the comparer-typed core without a per-call allocation: the
	// delegate is the caller's, the wrapper lives on the stack.
	private readonly struct ComparisonComparer<T> : IComparer<T> {
		private readonly Comparison<T> _comparison;

		public ComparisonComparer(Comparison<T> comparison) => _comparison = comparison;

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public int Compare(T? x, T? y) => _comparison(x!, y!);
	}

	// ── Index order ───────────────────────────────────────────────────────────

	private static void SortIndices<T, TComparer>(Span<int> order, Span<T> values, ref TComparer comparer, int depth)
		where TComparer : IComparer<T> {
		for (var i = 0; i < order.Length; i++)
			order[i] = i;

		IntroSort(ref MemoryMarshal.GetReference(order), order.Length, ref MemoryMarshal.GetReference(values), ref comparer, depth);
	}

	// Orders `length` indices at `first` by (row key, index): three-way partition by key, equal block
	// finished by an integer sort, outer blocks recursed (smaller one) / looped (larger one).
	private static void IntroSort<T, TComparer>(ref int first, int length, ref T rows, ref TComparer comparer, int depth)
		where TComparer : IComparer<T> {
		while (length > InsertionSortThreshold) {
			if (depth == 0) {
				// Adversarial partitioning: a heapsort by (key, index) is stable by construction of the order.
				HeapSort(ref first, length, ref rows, ref comparer);
				return;
			}

			depth--;
			Partition(ref first, length, ref rows, ref comparer, out var lessCount, out var greaterStart);

			// Indices whose rows equal the pivot: their encounter order is their numeric order.
			var equalCount = greaterStart - lessCount;
			if (equalCount > 1)
				MemoryMarshal.CreateSpan(ref Unsafe.Add(ref first, lessCount), equalCount).Sort();

			var greaterCount = length - greaterStart;
			if (lessCount < greaterCount) {
				IntroSort(ref first, lessCount, ref rows, ref comparer, depth);
				first = ref Unsafe.Add(ref first, greaterStart);
				length = greaterCount;
			} else {
				IntroSort(ref Unsafe.Add(ref first, greaterStart), greaterCount, ref rows, ref comparer, depth);
				length = lessCount;
			}
		}

		InsertionSort(ref first, length, ref rows, ref comparer);
	}

	// Dutch-flag partition of the indices around the key of a median-of-three pivot row:
	// [0, lessCount) rows < pivot, [lessCount, greaterStart) rows == pivot, [greaterStart, length) rows > pivot.
	// The equal block is never empty (it holds the pivot), so both outer blocks are strictly shorter.
	private static void Partition<T, TComparer>(ref int first, int length, ref T rows, ref TComparer comparer, out int lessCount, out int greaterStart)
		where TComparer : IComparer<T> {
		var last = length - 1;
		var middle = length >> 1;
		ref var a = ref first;
		ref var b = ref Unsafe.Add(ref first, middle);
		ref var c = ref Unsafe.Add(ref first, last);
		if (comparer.Compare(Unsafe.Add(ref rows, a), Unsafe.Add(ref rows, b)) > 0)
			Swap(ref a, ref b);

		if (comparer.Compare(Unsafe.Add(ref rows, a), Unsafe.Add(ref rows, c)) > 0)
			Swap(ref a, ref c);

		if (comparer.Compare(Unsafe.Add(ref rows, b), Unsafe.Add(ref rows, c)) > 0)
			Swap(ref b, ref c);

		var pivot = Unsafe.Add(ref rows, b);
		var less = 0;
		var i = 0;
		var greater = length;
		while (i < greater) {
			ref var index = ref Unsafe.Add(ref first, i);
			var order = comparer.Compare(Unsafe.Add(ref rows, index), pivot);
			if (order < 0) {
				Swap(ref Unsafe.Add(ref first, less), ref index);
				less++;
				i++;
			} else if (order > 0) {
				greater--;
				Swap(ref index, ref Unsafe.Add(ref first, greater));
			} else {
				i++;
			}
		}

		lessCount = less;
		greaterStart = greater;
	}

	// Stable total order on indices: row key first, encounter (index) order on ties.
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int CompareIndices<T, TComparer>(int x, int y, ref T rows, ref TComparer comparer)
		where TComparer : IComparer<T> {
		var order = comparer.Compare(Unsafe.Add(ref rows, x), Unsafe.Add(ref rows, y));
		return order != 0 ? order : x.CompareTo(y);
	}

	private static void InsertionSort<T, TComparer>(ref int first, int length, ref T rows, ref TComparer comparer)
		where TComparer : IComparer<T> {
		for (var i = 1; i < length; i++) {
			var index = Unsafe.Add(ref first, i);
			var j = i - 1;
			while (j >= 0 && CompareIndices(Unsafe.Add(ref first, j), index, ref rows, ref comparer) > 0) {
				Unsafe.Add(ref first, j + 1) = Unsafe.Add(ref first, j);
				j--;
			}

			Unsafe.Add(ref first, j + 1) = index;
		}
	}

	private static void HeapSort<T, TComparer>(ref int first, int length, ref T rows, ref TComparer comparer)
		where TComparer : IComparer<T> {
		for (var i = (length >> 1) - 1; i >= 0; i--)
			SiftDown(ref first, i, length, ref rows, ref comparer);

		for (var i = length - 1; i > 0; i--) {
			Swap(ref first, ref Unsafe.Add(ref first, i));
			SiftDown(ref first, 0, i, ref rows, ref comparer);
		}
	}

	private static void SiftDown<T, TComparer>(ref int first, int i, int size, ref T rows, ref TComparer comparer)
		where TComparer : IComparer<T> {
		while (true) {
			var largest = i;
			var left = 2 * i + 1;
			var right = left + 1;
			if (left < size && CompareIndices(Unsafe.Add(ref first, left), Unsafe.Add(ref first, largest), ref rows, ref comparer) > 0)
				largest = left;

			if (right < size && CompareIndices(Unsafe.Add(ref first, right), Unsafe.Add(ref first, largest), ref rows, ref comparer) > 0)
				largest = right;

			if (largest == i)
				return;

			Swap(ref Unsafe.Add(ref first, i), ref Unsafe.Add(ref first, largest));
			i = largest;
		}
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void Swap(ref int a, ref int b) {
		var t = a;
		a = b;
		b = t;
	}

	// ── Rows ──────────────────────────────────────────────────────────────────

	// values[i] = values[order[i]] for every i, through one pooled buffer.
	private static void Permute<T>(Span<T> values, ReadOnlySpan<int> order, Span<T> buffer) {
		ref var source = ref MemoryMarshal.GetReference(values);
		ref var target = ref MemoryMarshal.GetReference(buffer);
		ref var index = ref MemoryMarshal.GetReference(order);
		for (var i = 0; i < order.Length; i++)
			Unsafe.Add(ref target, i) = Unsafe.Add(ref source, Unsafe.Add(ref index, i));

		buffer.CopyTo(values);
	}

	private static void InsertionSortRows<T, TComparer>(Span<T> values, ref TComparer comparer)
		where TComparer : IComparer<T> {
		ref var first = ref MemoryMarshal.GetReference(values);
		for (var i = 1; i < values.Length; i++) {
			var item = Unsafe.Add(ref first, i);
			var j = i - 1;
			// Shift only strictly greater rows: an equal row stays ahead, which keeps the sort stable.
			while (j >= 0 && comparer.Compare(Unsafe.Add(ref first, j), item) > 0) {
				Unsafe.Add(ref first, j + 1) = Unsafe.Add(ref first, j);
				j--;
			}

			Unsafe.Add(ref first, j + 1) = item;
		}
	}

	private static void InsertionSortRows<T, TItem, TComparer>(Span<T> values, Span<TItem> items, ref TComparer comparer)
		where TComparer : IComparer<T> {
		ref var first = ref MemoryMarshal.GetReference(values);
		ref var firstItem = ref MemoryMarshal.GetReference(items);
		for (var i = 1; i < values.Length; i++) {
			var value = Unsafe.Add(ref first, i);
			var item = Unsafe.Add(ref firstItem, i);
			var j = i - 1;
			while (j >= 0 && comparer.Compare(Unsafe.Add(ref first, j), value) > 0) {
				Unsafe.Add(ref first, j + 1) = Unsafe.Add(ref first, j);
				Unsafe.Add(ref firstItem, j + 1) = Unsafe.Add(ref firstItem, j);
				j--;
			}

			Unsafe.Add(ref first, j + 1) = value;
			Unsafe.Add(ref firstItem, j + 1) = item;
		}
	}

	// ── Rows in place (keyed overload) ────────────────────────────────────────
	// The framework's keyed introsort (ArraySortHelper<TKey, TValue>) specialised on TComparer:
	// median-of-three pivot parked at hi - 1 as the scan sentinel, Hoare partition, heapsort past the
	// depth budget, insertion sort under the threshold. Unstable on its own; RestoreEncounterOrder
	// repairs the ties from the ordinals that travel as the items. Indexing stays bounds-checked so an
	// inconsistent comparer throws instead of scanning past the span.

	private static void IntroSortRows<T, TComparer>(Span<T> values, Span<int> items, ref TComparer comparer, int depth)
		where TComparer : IComparer<T> {
		var partitionSize = values.Length;
		while (partitionSize > 1) {
			if (partitionSize <= InsertionSortThreshold) {
				InsertionSortRows(values.Slice(0, partitionSize), items.Slice(0, partitionSize), ref comparer);
				return;
			}

			if (depth == 0) {
				HeapSortRows(values.Slice(0, partitionSize), items.Slice(0, partitionSize), ref comparer);
				return;
			}

			depth--;
			var p = PartitionRows(values.Slice(0, partitionSize), items.Slice(0, partitionSize), ref comparer);

			// Rows above the pivot recurse, rows below loop.
			var upper = p + 1;
			IntroSortRows(values.Slice(upper, partitionSize - upper), items.Slice(upper, partitionSize - upper), ref comparer, depth);
			partitionSize = p;
		}
	}

	// Returns the pivot's final position; [0, p) <= pivot, (p, length) >= pivot.
	private static int PartitionRows<T, TComparer>(Span<T> values, Span<int> items, ref TComparer comparer)
		where TComparer : IComparer<T> {
		var hi = values.Length - 1;
		var middle = hi >> 1;
		SwapRowsIfGreater(values, items, 0, middle, ref comparer);
		SwapRowsIfGreater(values, items, 0, hi, ref comparer);
		SwapRowsIfGreater(values, items, middle, hi, ref comparer);

		// values[0] <= pivot <= values[hi] bound both scans; the pivot itself sits at hi - 1 until the end.
		var pivot = values[middle];
		SwapRows(values, items, middle, hi - 1);
		var left = 0;
		var right = hi - 1;
		while (left < right) {
			do left++; while (comparer.Compare(values[left], pivot) < 0);
			do right--; while (comparer.Compare(pivot, values[right]) < 0);

			if (left >= right)
				break;

			SwapRows(values, items, left, right);
		}

		if (left != hi - 1)
			SwapRows(values, items, left, hi - 1);

		return left;
	}

	private static void HeapSortRows<T, TComparer>(Span<T> values, Span<int> items, ref TComparer comparer)
		where TComparer : IComparer<T> {
		var n = values.Length;
		for (var i = n >> 1; i >= 1; i--)
			DownHeapRows(values, items, i, n, ref comparer);

		for (var i = n; i > 1; i--) {
			SwapRows(values, items, 0, i - 1);
			DownHeapRows(values, items, 1, i - 1, ref comparer);
		}
	}

	// 1-based heap over values[0..n): sifts the row at i down to its place.
	private static void DownHeapRows<T, TComparer>(Span<T> values, Span<int> items, int i, int n, ref TComparer comparer)
		where TComparer : IComparer<T> {
		var value = values[i - 1];
		var item = items[i - 1];
		while (i <= n >> 1) {
			var child = 2 * i;
			if (child < n && comparer.Compare(values[child - 1], values[child]) < 0)
				child++;

			if (comparer.Compare(value, values[child - 1]) >= 0)
				break;

			values[i - 1] = values[child - 1];
			items[i - 1] = items[child - 1];
			i = child;
		}

		values[i - 1] = value;
		items[i - 1] = item;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void SwapRowsIfGreater<T, TComparer>(Span<T> values, Span<int> items, int i, int j, ref TComparer comparer)
		where TComparer : IComparer<T> {
		if (comparer.Compare(values[i], values[j]) > 0)
			SwapRows(values, items, i, j);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static void SwapRows<T>(Span<T> values, Span<int> items, int i, int j) {
		(values[i], values[j]) = (values[j], values[i]);
		(items[i], items[j]) = (items[j], items[i]);
	}
}
