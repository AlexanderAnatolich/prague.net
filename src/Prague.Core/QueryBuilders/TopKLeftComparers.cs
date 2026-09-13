namespace Prague.Core;

using System.Diagnostics;
using System.Runtime.CompilerServices;

/// <summary>
///   Orders <c>(left, ordinal)</c> pairs by the left value, then by encounter order, calling the
///   <b>user comparer</b> as a struct type parameter instead of reaching it through the sorter.
///   <para>
///   The hop this replaces is not free and not the obvious one. <c>IJoinResolver.CompareLeftValues</c>
///   is a <b>generic method</b>, so over a reference-type left value it compiles once as
///   <c>CompareLeftValues[__Canon]</c> — reached through a runtime generic dictionary, never inlined,
///   an indirect call per comparison — even when the sorter itself is a struct type parameter. The
///   bounded containers' own top-k workload (100 rows into a heap of 40) measured 4.01 µs through the
///   sorter against 1.95 µs with the comparer carried directly (<c>BoundedComparerProbeBenchmarks</c>).
///   </para>
///   <para>
///   <typeparamref name="TResult" /> is what the comparer orders and <typeparamref name="TValue" /> is
///   the left value; the bounded gate (<c>IJoinResolver.OrdersByLeftValues</c>) admits the container
///   only when they are the same type, which is what makes the reinterpret sound. A class comparer
///   takes the same path — one interface call, still no generic-dictionary lookup; a struct comparer
///   folds all the way down.
///   </para>
/// </summary>
internal readonly struct TopKLeftValueComparer<TValue, TResult, TComparer> : IComparer<(TValue Left, int Ordinal)>
	where TComparer : IComparer<TResult> {
	private readonly TComparer _comparer;

	public TopKLeftValueComparer(TComparer comparer) {
		Debug.Assert(typeof(TValue) == typeof(TResult), "the bounded gate admits this comparer only when the sorter orders the left value");
		_comparer = comparer;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int Compare((TValue Left, int Ordinal) x, (TValue Left, int Ordinal) y) {
		var order = _comparer.Compare(Unsafe.As<TValue, TResult>(ref x.Left), Unsafe.As<TValue, TResult>(ref y.Left));
		return order != 0 ? order : x.Ordinal.CompareTo(y.Ordinal);
	}
}

/// <summary>
///   Joined twin of <see cref="TopKLeftValueComparer{TValue,TResult,TComparer}" />: orders
///   <c>(key, left, ordinal)</c> triples by the left value, then by encounter order, calling the
///   <b>user comparer</b> as a struct type parameter instead of reaching it through the resolver chain.
///   <para>
///   Neither hop is free, and the deeper one is not the chain. <c>IResolvers.CompareLeftValues</c> costs
///   a JIT-folded <c>IsSorter</c> test per link; <c>IJoinResolver.CompareLeftValues</c>, at the end of
///   it, is a <b>generic method</b>, so over a reference-type left value it compiles once as
///   <c>CompareLeftValues[__Canon]</c> — reached through a runtime generic dictionary, never inlined,
///   an indirect call per comparison. Measured on shape A's real page path (the ceiling benchmark's
///   level 2, the real container and the real steps): 3,735 ns through the sorter against 2,377 ns with
///   a direct struct comparer, <b>1,359 ns</b> — a third of level 2. The disassembly of
///   <c>TopKSelect.Partition</c> is the proof: five indirect <c>blr</c> calls and four
///   <c>CORINFO_HELP_RUNTIMEHANDLE_METHOD</c> helpers on the sorter instantiation, none at all on the
///   direct one, where the user comparer is inlined into the partition body. The chain hop on top of it
///   is what the per-link walk costs: the same workload measured 3.81 µs through the sorter against
///   5.00 / 7.43 / 9.62 µs through a 1 / 2 / 3-link chain (<c>BoundedComparerProbeBenchmarks</c>).
///   </para>
///   <para>
///   <typeparamref name="TResult" /> is what the comparer orders and <typeparamref name="TValue" /> is
///   the left value; the bounded gate (<c>JoinChainShape.BoundedCapable</c> / the eager
///   <c>TopKProbeProcessor</c>, both over <c>OrdersByLeftValues</c>) admits this container only when
///   they are the same type, which is what makes the reinterpret sound. A class comparer takes the same
///   path — one interface call, still no generic-dictionary lookup; a struct comparer folds all the way
///   down.
///   </para>
/// </summary>
internal readonly struct TopKLeftPairComparer<TKey, TValue, TResult, TComparer> : IComparer<(TKey Key, TValue Left, int Ordinal)>
	where TComparer : IComparer<TResult> {
	private readonly TComparer _comparer;

	public TopKLeftPairComparer(TComparer comparer) {
		Debug.Assert(typeof(TValue) == typeof(TResult), "the bounded gate admits this container only when the sorter orders the left value");
		_comparer = comparer;
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int Compare((TKey Key, TValue Left, int Ordinal) x, (TKey Key, TValue Left, int Ordinal) y) {
		var order = _comparer.Compare(Unsafe.As<TValue, TResult>(ref x.Left), Unsafe.As<TValue, TResult>(ref y.Left));
		return order != 0 ? order : x.Ordinal.CompareTo(y.Ordinal);
	}
}
