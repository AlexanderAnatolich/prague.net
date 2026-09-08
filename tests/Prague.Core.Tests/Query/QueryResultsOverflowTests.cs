namespace Prague.Core.Tests.Query;

using System.Runtime.CompilerServices;
using Prague.Core;
using NUnit.Framework;

/// <summary>
///   A join slot is a fixed partition of one shared buffer, so it cannot grow. When a resolver
///   sizes it from a live index count and the index is written before the rows are delivered, the
///   slot is handed more rows than it reserved. The contract is that the query never fails
///   (context/joins.md, "Concurrency"): the row is dropped, the result says so, the drop is counted.
/// </summary>
[TestFixture]
// QueryResultsDiagnostics is process-global state.
[NonParallelizable]
public class QueryResultsOverflowTests {
	[SetUp]
	public void SetUp() => QueryResultsDiagnostics.ResetDroppedRows();

	[TearDown]
	public void TearDown() => QueryResultsDiagnostics.ThrowOnSlotOverflowInDebug = true;

	[Test]
	public void UnsafeAdd_PastCapacity_DropsTheRowInsteadOfThrowing() {
		QueryResultsDiagnostics.ThrowOnSlotOverflowInDebug = false;
		var results = new QueryResults<int>(2, shouldPool: false);

		Assert.That(results.UnsafeAdd(10), Is.EqualTo(0));
		Assert.That(results.UnsafeAdd(20), Is.EqualTo(1));

		Assert.That(results.UnsafeAdd(30), Is.EqualTo(-1), "a dropped row reports no write index");
		Assert.Multiple(() => {
			Assert.That(results.Count, Is.EqualTo(2), "the dropped row must not be counted");
			Assert.That(results.TotalCount, Is.EqualTo(2));
			Assert.That(results.Truncated, Is.True);
			Assert.That(QueryResultsDiagnostics.DroppedRows, Is.EqualTo(1));
			Assert.That(results.AsSpan().ToArray(), Is.EqualTo(new[] { 10, 20 }), "the rows that fit are intact");
		});
	}

	[Test]
	public void UnsafeAdd_WithinCapacity_LeavesTheResultUntruncated() {
		var results = new QueryResults<int>(2, shouldPool: false);
		results.UnsafeAdd(10);
		results.UnsafeAdd(20);

		Assert.Multiple(() => {
			Assert.That(results.Truncated, Is.False);
			Assert.That(QueryResultsDiagnostics.DroppedRows, Is.Zero, "a full slot is not a truncated one");
		});
	}

	[Test]
	public void UnsafeAdd_PastCapacity_KeepsCountingEveryDrop() {
		QueryResultsDiagnostics.ThrowOnSlotOverflowInDebug = false;
		var results = new QueryResults<int>(1, shouldPool: false);
		results.UnsafeAdd(1);

		for (var i = 0; i < 3; i++)
			results.UnsafeAdd(i);

		Assert.That(QueryResultsDiagnostics.DroppedRows, Is.EqualTo(3));
	}

#if DEBUG
	// The drop is silent by contract in release builds, so DEBUG is where a sizing defect has to
	// be loud — otherwise the four resolvers that still size from a live count could regress unseen.
	[Test]
	public void UnsafeAdd_PastCapacity_ThrowsInDebugByDefault() {
		var results = new QueryResults<int>(1, shouldPool: false);
		results.UnsafeAdd(1);

		Assert.That(() => results.UnsafeAdd(2), Throws.InstanceOf<InvalidOperationException>());
		Assert.That(QueryResultsDiagnostics.DroppedRows, Is.EqualTo(1), "the drop is recorded before it is asserted on");
	}
#endif

	// _truncated is declared next to _isPooled so it lands in that bool's tail padding. QueryResults
	// is embedded per-left in every joined result slot, so this staying free is load-bearing.
	[Test]
	public void QueryResults_IsNoLargerForCarryingTheTruncatedFlag() {
		Assert.Multiple(() => {
			Assert.That(Unsafe.SizeOf<QueryResults<int>>(), Is.EqualTo(88));
			Assert.That(Unsafe.SizeOf<QueryResults<object>>(), Is.EqualTo(88));
		});
	}
}
