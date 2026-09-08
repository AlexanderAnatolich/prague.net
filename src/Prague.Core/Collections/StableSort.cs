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
///   framework's unstable introsort on distinct keys and beat it on heavy ties. Comparisons go through
///   a <see cref="Comparison{T}"/> built once per sort — the delegate the framework sort builds as
///   well — because the JIT devirtualizes a hot delegate target where a constrained interface call
///   through a wrapper struct stays a virtual dispatch.
///   </para>
///   <para>
///   The keyed overload — the joined pipeline's row dictionary, arrays of result structs several
///   megabytes long — sorts the values IN PLACE with the framework introsort, moving a pooled ordinal
///   per row as the sort's items, then restores encounter order inside every run of equal values by
///   sorting the runs' ordinals — integers only — and gathering the rows into that order once; the
///   caller's items follow the ordinals through one more permutation pass. Sorting indices there cost
///   half again the framework sort: every comparison took an extra random hop into the struct rows,
///   which the in-place sort reads sequentially.
///   </para>
///   A comparison that throws leaves the rows in an unspecified permutation like the framework sort
///   (the single-span overload leaves them untouched unless it throws inside the final permutation).
/// </summary>
internal static class StableSort {
	// Below this many rows the index machinery does not pay for itself: a stable insertion sort on the
	// rows themselves finishes the job.
	private const int DirectInsertionThreshold = 32;

	// Index ranges this short are insertion-sorted by (key, index) instead of partitioned.
	private const int InsertionSortThreshold = 16;

	/// <summary>
	///   Sorts <paramref name="values"/> ascending; equal items keep their relative order. A null
	///   <paramref name="comparer"/> means <see cref="Comparer{T}.Default"/>, as it does for
	///   <c>Span&lt;T&gt;.Sort</c>.
	/// </summary>
	internal static void Sort<T, TComparer>(Span<T> values, TComparer comparer)
		where TComparer : IComparer<T> {
		if (values.Length < 2) {
			return;
		}

		if (comparer is null) {
			Sort(values, Comparer<T>.Default);
			return;
		}

		Sort(values, new Comparison<T>(comparer.Compare));
	}

	/// <summary>
	///   Sorts <paramref name="values"/> ascending and moves <paramref name="items"/> alongside, so
	///   <c>items[i]</c> still belongs to <c>values[i]</c> afterwards; equal values keep their order. A
	///   null <paramref name="comparer"/> means <see cref="Comparer{T}.Default"/>.
	/// </summary>
	internal static void Sort<T, TItem, TComparer>(Span<T> values, Span<TItem> items, TComparer comparer)
		where TComparer : IComparer<T> {
		if (values.Length != items.Length) {
			throw new ArgumentException("values and items must have the same length", nameof(items));
		}

		var n = values.Length;
		if (n < 2) {
			return;
		}

		if (comparer is null) {
			Sort(values, items, Comparer<T>.Default);
			return;
		}

		if (n <= DirectInsertionThreshold) {
			InsertionSortRows(values, items, comparer);
			return;
		}

		var ordinals = PragueArrayPool<int>.Pool.Rent(n);
		var itemBuffer = PragueArrayPool<TItem>.Pool.Rent(n);
		try {
			var order = ordinals.AsSpan(0, n);
			for (var i = 0; i < n; i++) {
				order[i] = i;
			}

			// In place, unstable: the ordinals ride along as the sort's items and record where each row
			// came from.
			values.Sort(order, comparer);
			RestoreEncounterOrder(values, order, comparer);
			Permute(items, order, itemBuffer.AsSpan(0, n));
		} finally {
			PragueArrayPool<int>.Pool.Return(ordinals);
			PragueArrayPool<TItem>.Pool.Return(itemBuffer, RuntimeHelpers.IsReferenceOrContainsReferences<TItem>());
		}
	}

	// After the unstable in-place sort every run of values the comparer calls equal is contiguous, and
	// order[i] is the encounter position of the row now at i. Within a run the stable order is ascending
	// encounter position: sort each run's positions — integers, no user comparisons, no row moves — and,
	// once any run longer than one row exists, gather the rows into the corrected order in one pass.
	// Distinct keys make every run one row long and cost only the adjacent comparison that finds them.
	private static void RestoreEncounterOrder<T, TComparer>(Span<T> values, Span<int> order, TComparer comparer)
		where TComparer : IComparer<T> {
		var n = values.Length;
		int[]? unstable = null; // unstable[encounter position] = index the row holds after the in-place sort
		try {
			var runStart = 0;
			for (var i = 1; i <= n; i++) {
				if (i < n && comparer.Compare(values[i - 1], values[i]) == 0) {
					continue;
				}

				var length = i - runStart;
				if (length > 1) {
					if (unstable is null) {
						// First tie: remember where every row sits before any run is re-ordered.
						unstable = PragueArrayPool<int>.Pool.Rent(n);
						for (var k = 0; k < n; k++) {
							unstable[order[k]] = k;
						}
					}

					order.Slice(runStart, length).Sort();
				}

				runStart = i;
			}

			if (unstable is null) {
				return;
			}

			var buffer = PragueArrayPool<T>.Pool.Rent(n);
			try {
				var gathered = buffer.AsSpan(0, n);
				for (var k = 0; k < n; k++) {
					gathered[k] = values[unstable[order[k]]];
				}

				gathered.CopyTo(values);
			} finally {
				PragueArrayPool<T>.Pool.Return(buffer, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
			}
		} finally {
			if (unstable is not null) {
				PragueArrayPool<int>.Pool.Return(unstable);
			}
		}
	}

	internal static void Sort<T>(Span<T> values, Comparison<T> comparison)
		=> Sort(values, comparison, DepthLimit(values.Length));

	/// <summary>Test seam: <paramref name="depthLimit"/> bounds the partitioning depth (0 forces the heapsort fallback).</summary>
	internal static void Sort<T>(Span<T> values, Comparison<T> comparison, int depthLimit) {
		var n = values.Length;
		if (n < 2) {
			return;
		}

		if (n <= DirectInsertionThreshold) {
			InsertionSortRows(values, comparison);
			return;
		}

		var indices = PragueArrayPool<int>.Pool.Rent(n);
		var buffer = PragueArrayPool<T>.Pool.Rent(n);
		try {
			var order = indices.AsSpan(0, n);
			SortIndices(order, values, comparison, depthLimit);
			Permute(values, order, buffer.AsSpan(0, n));
		} finally {
			PragueArrayPool<int>.Pool.Return(indices);
			PragueArrayPool<T>.Pool.Return(buffer, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
		}
	}

	// Same budget as the framework introsort: past it, partitioning is assumed adversarial.
	private static int DepthLimit(int length) => 2 * (BitOperations.Log2((uint)length) + 1);

	// ── Index order ───────────────────────────────────────────────────────────

	private static void SortIndices<T>(Span<int> order, Span<T> values, Comparison<T> comparison, int depth) {
		for (var i = 0; i < order.Length; i++) {
			order[i] = i;
		}

		IntroSort(ref MemoryMarshal.GetReference(order), order.Length, ref MemoryMarshal.GetReference(values), comparison, depth);
	}

	// Orders `length` indices at `first` by (row key, index): three-way partition by key, equal block
	// finished by an integer sort, outer blocks recursed (smaller one) / looped (larger one).
	private static void IntroSort<T>(ref int first, int length, ref T rows, Comparison<T> comparison, int depth) {
		while (length > InsertionSortThreshold) {
			if (depth == 0) {
				// Adversarial partitioning: a heapsort by (key, index) is stable by construction of the order.
				HeapSort(ref first, length, ref rows, comparison);
				return;
			}

			depth--;
			Partition(ref first, length, ref rows, comparison, out var lessCount, out var greaterStart);

			// Indices whose rows equal the pivot: their encounter order is their numeric order.
			var equalCount = greaterStart - lessCount;
			if (equalCount > 1) {
				MemoryMarshal.CreateSpan(ref Unsafe.Add(ref first, lessCount), equalCount).Sort();
			}

			var greaterCount = length - greaterStart;
			if (lessCount < greaterCount) {
				IntroSort(ref first, lessCount, ref rows, comparison, depth);
				first = ref Unsafe.Add(ref first, greaterStart);
				length = greaterCount;
			} else {
				IntroSort(ref Unsafe.Add(ref first, greaterStart), greaterCount, ref rows, comparison, depth);
				length = lessCount;
			}
		}

		InsertionSort(ref first, length, ref rows, comparison);
	}

	// Dutch-flag partition of the indices around the key of a median-of-three pivot row:
	// [0, lessCount) rows < pivot, [lessCount, greaterStart) rows == pivot, [greaterStart, length) rows > pivot.
	// The equal block is never empty (it holds the pivot), so both outer blocks are strictly shorter.
	private static void Partition<T>(ref int first, int length, ref T rows, Comparison<T> comparison, out int lessCount, out int greaterStart) {
		var last = length - 1;
		var middle = length >> 1;
		ref var a = ref first;
		ref var b = ref Unsafe.Add(ref first, middle);
		ref var c = ref Unsafe.Add(ref first, last);
		if (comparison(Unsafe.Add(ref rows, a), Unsafe.Add(ref rows, b)) > 0) {
			Swap(ref a, ref b);
		}

		if (comparison(Unsafe.Add(ref rows, a), Unsafe.Add(ref rows, c)) > 0) {
			Swap(ref a, ref c);
		}

		if (comparison(Unsafe.Add(ref rows, b), Unsafe.Add(ref rows, c)) > 0) {
			Swap(ref b, ref c);
		}

		var pivot = Unsafe.Add(ref rows, b);
		var less = 0;
		var i = 0;
		var greater = length;
		while (i < greater) {
			ref var index = ref Unsafe.Add(ref first, i);
			var order = comparison(Unsafe.Add(ref rows, index), pivot);
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
	private static int CompareIndices<T>(int x, int y, ref T rows, Comparison<T> comparison) {
		var order = comparison(Unsafe.Add(ref rows, x), Unsafe.Add(ref rows, y));
		return order != 0 ? order : x.CompareTo(y);
	}

	private static void InsertionSort<T>(ref int first, int length, ref T rows, Comparison<T> comparison) {
		for (var i = 1; i < length; i++) {
			var index = Unsafe.Add(ref first, i);
			var j = i - 1;
			while (j >= 0 && CompareIndices(Unsafe.Add(ref first, j), index, ref rows, comparison) > 0) {
				Unsafe.Add(ref first, j + 1) = Unsafe.Add(ref first, j);
				j--;
			}

			Unsafe.Add(ref first, j + 1) = index;
		}
	}

	private static void HeapSort<T>(ref int first, int length, ref T rows, Comparison<T> comparison) {
		for (var i = (length >> 1) - 1; i >= 0; i--) {
			SiftDown(ref first, i, length, ref rows, comparison);
		}

		for (var i = length - 1; i > 0; i--) {
			Swap(ref first, ref Unsafe.Add(ref first, i));
			SiftDown(ref first, 0, i, ref rows, comparison);
		}
	}

	private static void SiftDown<T>(ref int first, int i, int size, ref T rows, Comparison<T> comparison) {
		while (true) {
			var largest = i;
			var left = 2 * i + 1;
			var right = left + 1;
			if (left < size && CompareIndices(Unsafe.Add(ref first, left), Unsafe.Add(ref first, largest), ref rows, comparison) > 0) {
				largest = left;
			}

			if (right < size && CompareIndices(Unsafe.Add(ref first, right), Unsafe.Add(ref first, largest), ref rows, comparison) > 0) {
				largest = right;
			}

			if (largest == i) {
				return;
			}

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
		for (var i = 0; i < order.Length; i++) {
			Unsafe.Add(ref target, i) = Unsafe.Add(ref source, Unsafe.Add(ref index, i));
		}

		buffer.CopyTo(values);
	}

	private static void InsertionSortRows<T>(Span<T> values, Comparison<T> comparison) {
		ref var first = ref MemoryMarshal.GetReference(values);
		for (var i = 1; i < values.Length; i++) {
			var item = Unsafe.Add(ref first, i);
			var j = i - 1;
			// Shift only strictly greater rows: an equal row stays ahead, which keeps the sort stable.
			while (j >= 0 && comparison(Unsafe.Add(ref first, j), item) > 0) {
				Unsafe.Add(ref first, j + 1) = Unsafe.Add(ref first, j);
				j--;
			}

			Unsafe.Add(ref first, j + 1) = item;
		}
	}

	private static void InsertionSortRows<T, TItem, TComparer>(Span<T> values, Span<TItem> items, TComparer comparer)
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
}
