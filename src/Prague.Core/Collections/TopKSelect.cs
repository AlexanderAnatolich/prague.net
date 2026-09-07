namespace Prague.Core.Collections;

using System.Runtime.CompilerServices;

/// <summary>
///   Bounded top-K selection over a caller-owned buffer: keeps the K smallest items
///   per <typeparamref name="TComparer"/> in a max-heap (root = worst kept item).
///   Candidates not better than the root are rejected in O(1); accepted candidates
///   replace the root and sift down in O(log K). <see cref="DrainAscending{T,TComparer}"/>
///   heapsorts the kept items in place, ascending.
///
///   Pure static helpers over caller-owned state — no rents, no ownership, so the
///   struct-copy double-return hazard that killed the old TopKHeap cannot occur.
///   Complexity: O(M log K) pushes for M candidates, O(K log K) drain, zero allocation.
/// </summary>
internal static class TopKSelect {
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	internal static void Push<T, TComparer>(T[] buffer, ref int count, ref bool heapified, int k, T item, TComparer comparer)
		where TComparer : IComparer<T> {
		if (k <= 0) {
			return;
		}

		if (count < k) {
			buffer[count++] = item;
			if (count == k) {
				BuildMaxHeap(buffer, count, comparer);
				heapified = true;
			}

			return;
		}

		// Heap is full — accept only items strictly better (smaller) than the current worst.
		if (comparer.Compare(item, buffer[0]) >= 0) {
			return;
		}

		buffer[0] = item;
		SiftDown(buffer, 0, count, comparer);
	}

	/// <summary>Heapsorts the kept items in place (ascending). Returns the row count; resets state.</summary>
	internal static int DrainAscending<T, TComparer>(T[] buffer, ref int count, ref bool heapified, TComparer comparer)
		where TComparer : IComparer<T> {
		var n = count;
		if (n == 0) {
			return 0;
		}

		if (!heapified) {
			BuildMaxHeap(buffer, n, comparer);
		}

		for (var i = n - 1; i > 0; i--) {
			(buffer[0], buffer[i]) = (buffer[i], buffer[0]);
			SiftDown(buffer, 0, i, comparer);
		}

		count = 0;
		heapified = false;
		return n;
	}

	private static void BuildMaxHeap<T, TComparer>(T[] buffer, int size, TComparer comparer)
		where TComparer : IComparer<T> {
		for (var i = size / 2 - 1; i >= 0; i--) {
			SiftDown(buffer, i, size, comparer);
		}
	}

	private static void SiftDown<T, TComparer>(T[] buffer, int i, int size, TComparer comparer)
		where TComparer : IComparer<T> {
		while (true) {
			var largest = i;
			var left = 2 * i + 1;
			var right = 2 * i + 2;

			if (left < size && comparer.Compare(buffer[left], buffer[largest]) > 0) {
				largest = left;
			}

			if (right < size && comparer.Compare(buffer[right], buffer[largest]) > 0) {
				largest = right;
			}

			if (largest == i) {
				return;
			}

			(buffer[i], buffer[largest]) = (buffer[largest], buffer[i]);
			i = largest;
		}
	}
}
