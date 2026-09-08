namespace Prague.Core;

using System.Diagnostics;
using System.Threading;

/// <summary>
/// Process-wide counters for the one thing a query is allowed to do silently: drop a joined row
/// because its slot was already full.
///
/// A join slot is a fixed partition of one shared buffer. When a resolver sizes that partition
/// from a live index count and the index is written before the rows are delivered, the slot can
/// be handed more rows than it reserved. A query must never fail (see context/joins.md,
/// "Concurrency"), so the extra row is dropped — but a drop is always a sizing defect somewhere,
/// so it is counted here and surfaced per-result as <c>QueryResults{T}.Truncated</c>.
///
/// <see cref="DroppedRows"/> is expected to be 0. A non-zero value in production means a resolver
/// is under-sizing slots; the counter is the only trace such a row leaves.
/// </summary>
public static class QueryResultsDiagnostics {
	private static long _droppedRows;

	/// <summary>Total rows dropped by full join slots since process start. Expected to be 0.</summary>
	public static long DroppedRows => Volatile.Read(ref _droppedRows);

	/// <summary>
	/// When true (the default), a dropped row throws in DEBUG builds so a sizing defect fails a
	/// test instead of quietly losing a row. Compiled out of release builds entirely, where the
	/// never-fail contract applies. Tests that exercise known-lossy resolvers clear it.
	/// </summary>
	public static bool ThrowOnSlotOverflowInDebug { get; set; } = true;

	/// <summary>Resets <see cref="DroppedRows"/>. For tests that assert on the counter.</summary>
	public static void ResetDroppedRows() => Volatile.Write(ref _droppedRows, 0);

	internal static void RecordDroppedRow() => Interlocked.Increment(ref _droppedRows);

	// [Conditional] removes the call from release builds of this assembly, so the throw cannot
	// reach production no matter how the property is set.
	[Conditional("DEBUG")]
	internal static void AssertNoSlotOverflow(int count, int capacity) {
		if (ThrowOnSlotOverflowInDebug)
			throw new InvalidOperationException(
				$"Join slot overflow: count={count}, capacity={capacity}. A resolver sized this slot below the "
				+ "rows it delivered. Release builds drop the row instead of throwing; set "
				+ $"{nameof(QueryResultsDiagnostics)}.{nameof(ThrowOnSlotOverflowInDebug)} = false to exercise that path.");
	}
}
