// docs: every code block of www/docs/articles/explanation/ordering-and-determinism.md. Each #region is
// one claim of the article and the assertions after it are the proof; a claim with no order guarantee
// is asserted as a set (Is.EquivalentTo), a claim with one as a sequence (Is.EqualTo).
namespace Prague.Samples.Explanation;

using NUnit.Framework;
using Prague.Core;
using Prague.Samples.Models;

[TestFixture]
public class Ordering {
	[Test]
	public void ManyIndexKeepsInsertionOrder() {
		var cache = new OrderCache();

		#region many-index-insertion-order
		cache.AddOrUpdate(NewOrder("ORD-003", 42));
		cache.AddOrUpdate(NewOrder("ORD-001", 42));
		cache.AddOrUpdate(NewOrder("ORD-002", 42));

		var rows = cache.Query().WithCustomerId(42).Execute();           // 003, 001, 002 — the bucket's slot order
		#endregion

		Assert.That(Ids(rows), Is.EqualTo(new[] { "ORD-003", "ORD-001", "ORD-002" }));
		Assert.That(cache.CustomerIdIndex.GetValues(42), Is.EqualTo(new[] { "ORD-003", "ORD-001", "ORD-002" }));

		#region slot-reuse-breaks-insertion-order
		cache.Remove("ORD-001");                                          // frees the middle slot
		cache.AddOrUpdate(NewOrder("ORD-004", 42));                       // takes it: 003, 004, 002

		rows = cache.Query().WithCustomerId(42).Execute();
		#endregion

		Assert.That(Ids(rows), Is.EqualTo(new[] { "ORD-003", "ORD-004", "ORD-002" }));
	}

	[Test]
	public void FirstLaneDecidesTheOrder() {
		var cache = TwoLaneFixture();

		#region first-lane-decides-the-order
		// Customer 42's bucket: 001 … 006 in insertion order. The Pending bucket: 002, 003, 007, then 001,
		// which moved to Pending last. The survivors come out in the first lane's order.
		var byCustomerFirst = cache.Query().WithCustomerId(42).WithStatus(OrderStatus.Pending).Execute();
		var byStatusFirst = cache.Query().WithStatus(OrderStatus.Pending).WithCustomerId(42).Execute();
		#endregion

		Assert.That(Ids(byCustomerFirst), Is.EqualTo(new[] { "ORD-001", "ORD-002", "ORD-003" }));
		Assert.That(Ids(byStatusFirst), Is.EqualTo(new[] { "ORD-002", "ORD-003", "ORD-001" }));
	}

	[Test]
	public void RangeLaneWalksAscending() {
		var cache = new OrderCache();
		cache.AddOrUpdate(NewOrder("ORD-002", 42, createdAt: 200));
		cache.AddOrUpdate(NewOrder("ORD-003", 42, createdAt: 300));
		cache.AddOrUpdate(NewOrder("ORD-001", 42, createdAt: 100));

		#region range-lane-ascending
		var rows = cache.Query().WithCreatedAtUnixMs(q => q.Gte(0L)).Execute();   // 001, 002, 003 — tree order, not insertion
		#endregion

		Assert.That(Ids(rows), Is.EqualTo(new[] { "ORD-001", "ORD-002", "ORD-003" }));
	}

	[Test]
	public void NoLaneWalksTheStore() {
		var cache = TwoLaneFixture();

		#region no-lane-store-order
		// A query with no index lane walks the store's hash table. The rows are right, their sequence is
		// the table's and moves when it resizes — treat it as a set.
		var all = cache.Query().Where(static o => o.Total > 0).Execute();
		#endregion

		Assert.That(Ids(all), Is.EquivalentTo(new[] { "ORD-001", "ORD-002", "ORD-003", "ORD-004", "ORD-005", "ORD-006", "ORD-007" }));
		Assert.That(Ids(cache.Query().Where(static o => o.Total > 0).Execute()), Is.EqualTo(Ids(all)));   // same table, same walk
	}

	[Test]
	public void SortIsStable() {
		var cache = SortFixture();

		#region sort-is-stable
		// ByStatus orders Pending before Paid and nothing else. Comparer-equal rows keep their encounter
		// order — the customer bucket's: 002, 003 (Pending) then 001, 004 (Paid).
		var sorted = cache.Query().WithCustomerId(42).Sort(default(ByStatus)).Execute();
		var bounded = cache.Query().WithCustomerId(42).SortBounded(default(ByStatus)).Execute(skip: 0, take: 4);
		#endregion

		Assert.That(Ids(sorted), Is.EqualTo(new[] { "ORD-002", "ORD-003", "ORD-001", "ORD-004" }));
		Assert.That(Ids(bounded), Is.EqualTo(Ids(sorted)));
	}

	[Test]
	public void PagesPartitionTheResult() {
		var cache = SortFixture();

		#region pages-partition
		var whole = cache.Query().WithCustomerId(42).Sort(default(ByStatus)).Execute();
		var page1 = cache.Query().WithCustomerId(42).SortBounded(default(ByStatus)).Execute(skip: 0, take: 2);
		var page2 = cache.Query().WithCustomerId(42).SortBounded(default(ByStatus)).Execute(skip: 2, take: 2);
		#endregion

		Assert.That(Ids(page1).Concat(Ids(page2)), Is.EqualTo(Ids(whole)));
		Assert.That(page1.TotalCount, Is.EqualTo(4));
		Assert.That(page2.TotalCount, Is.EqualTo(4));
	}

	[Test]
	public void PreparedReplaysTheEagerSequence() {
		var cache = TwoLaneFixture();

		#region prepared-replays-eager
		// Build() records the chain and replays it into the eager core on every execution, so a prepared
		// query's rows come out exactly as the eager spelling's — same lanes, same order.
		var eager = cache.Query().WithStatus(OrderStatus.Pending).WithCustomerId(42).Execute();

		var prepared = cache.Prepare<int>()
			.WithStatus(OrderStatus.Pending)
			.WithCustomerId(static a => a)
			.Build();
		using var rows = prepared.ExecutePooled(42);
		#endregion

		Assert.That(Ids(rows), Is.EqualTo(Ids(eager)));
		Assert.That(Ids(rows), Is.EqualTo(new[] { "ORD-002", "ORD-003", "ORD-001" }));
	}

	[Test]
	public void FrozenTiesAreUnspecified() {
		var cache = SortFixture();

		#region frozen-ties-unspecified
		// The comparer decides the groups; nothing decides the order inside a group. Assert what the
		// comparer says — Pending before Paid — and read each tie group as a set.
		var frozen = cache.Prepare()
			.WithCustomerId(42)
			.Sort(default(ByStatus))
			.BuildFrozen();
		using var rows = frozen.ExecutePooled();
		#endregion

		Assert.That(rows.Count, Is.EqualTo(4));
		Assert.That(rows.Take(2).Select(static o => o.Status), Is.All.EqualTo(OrderStatus.Pending));
		Assert.That(rows.Skip(2).Select(static o => o.Status), Is.All.EqualTo(OrderStatus.Paid));
		Assert.That(Ids(rows).Take(2), Is.EquivalentTo(new[] { "ORD-002", "ORD-003" }));
	}

	[Test]
	public void FrozenUnsortedIsASet() {
		var cache = TwoLaneFixture();

		#region frozen-unsorted-no-order
		// Same rows, same Count — but the frozen pipeline seeds from the narrowest lane for this
		// execution, so the sequence is not the eager one and is not promised.
		var eager = cache.Query().WithCustomerId(42).WithStatus(OrderStatus.Pending).Execute();

		var frozen = cache.Prepare<int>()
			.WithCustomerId(static a => a)
			.WithStatus(OrderStatus.Pending)
			.BuildFrozen();
		using var rows = frozen.ExecutePooled(42);
		#endregion

		Assert.That(Ids(rows), Is.EquivalentTo(Ids(eager)));
		Assert.That(rows.Count, Is.EqualTo(eager.Count));
		Assert.That(frozen.Explain(), Does.Contain("Pipeline"));
	}

	[Test]
	public void PreserveEagerOrderRestoresTheSequence() {
		var cache = TwoLaneFixture();
		var eager = cache.Query().WithCustomerId(42).WithStatus(OrderStatus.Pending).Execute();

		#region preserve-eager-order
		var frozen = cache.Prepare<int>()
			.WithCustomerId(static a => a)
			.WithStatus(OrderStatus.Pending)
			.BuildFrozen(new FrozenOptions { PreserveEagerOrder = true });    // seed pinned to the first declared lane
		using var rows = frozen.ExecutePooled(42);
		#endregion

		Assert.That(Ids(rows), Is.EqualTo(Ids(eager)));
	}

	[Test]
	public void FrozenSortedWithATotalComparerIsByteIdentical() {
		var cache = SortFixture();

		#region frozen-total-comparer
		// ByCreatedAtComparer comes from [DataCacheSort("ByCreatedAt", "CreatedAtUnixMs:-1", "OrderId:1")]:
		// the OrderId tiebreak makes it total, so eager and frozen agree row for row.
		var eager = cache.Query().WithCustomerId(42).SortBounded(OrderCache.ByCreatedAtComparer).Execute(skip: 0, take: 3);

		var frozen = cache.Prepare()
			.WithCustomerId(42)
			.SortBounded(OrderCache.ByCreatedAtComparer)
			.BuildFrozen();
		using var page = frozen.ExecutePooled(skip: 0, take: 3);
		#endregion

		Assert.That(Ids(page), Is.EqualTo(Ids(eager)));
		Assert.That(Ids(page), Is.EqualTo(new[] { "ORD-004", "ORD-003", "ORD-002" }));
	}

	[Test]
	public void JoinRowsFollowTheBaseWalk() {
		var registry = new DataCacheRegistryBuilder().Register<OrderCache>().Build();
		var orders = registry.GetCache<OrderCache>();
		var customers = registry.GetCache<CustomerCache>();
		var lines = registry.GetCache<OrderLineCache>();
		customers.AddOrUpdate(new Customer { Id = 42, Email = "a@example.com", Country = "CZ", Tier = CustomerTier.Gold, Name = "A" });
		orders.AddOrUpdate(NewOrder("ORD-003", 42));
		orders.AddOrUpdate(NewOrder("ORD-001", 42));
		orders.AddOrUpdate(NewOrder("ORD-002", 42));
		lines.AddOrUpdate(NewLine("ORD-001", 3));
		lines.AddOrUpdate(NewLine("ORD-001", 1));
		lines.AddOrUpdate(NewLine("ORD-001", 2));

		#region join-rows-follow-the-base-walk
		// A JoinOne fills a slot on each row the base walk produced; the rows keep the base order.
		var plain = orders.Query().WithCustomerId(42).Execute();
		using var joined = orders.Query().WithCustomerId(42).JoinWithCustomer().ExecutePooled();
		#endregion

		Assert.That(joined.Select(static r => r.Left.OrderId), Is.EqualTo(Ids(plain)));
		Assert.That(joined.All(static r => r.Right?.Id == 42), Is.True);

		#region join-many-slot-is-a-set
		// The lines of one order arrive as a QueryResults<OrderLine> slot. Read it as a set: eager fills
		// it in pair-set order and the frozen fused fill in bucket order — the same lines either way.
		using var withLines = orders.Query().WithKey("ORD-001").JoinWithOrderLine().ExecutePooled();
		var lineIds = withLines[0].Right!.Select(static l => l.LineId).ToArray();
		#endregion

		Assert.That(lineIds, Is.EquivalentTo(new[] { "ORD-001/3", "ORD-001/1", "ORD-001/2" }));
	}

	// Customer 42: 001 Paid → Pending last, 002 / 003 Pending, 004–006 Paid; customer 7: 007 Pending.
	private static OrderCache TwoLaneFixture() {
		var cache = new OrderCache();
		cache.AddOrUpdate(NewOrder("ORD-001", 42, OrderStatus.Paid));
		cache.AddOrUpdate(NewOrder("ORD-002", 42));
		cache.AddOrUpdate(NewOrder("ORD-003", 42));
		cache.AddOrUpdate(NewOrder("ORD-004", 42, OrderStatus.Paid));
		cache.AddOrUpdate(NewOrder("ORD-005", 42, OrderStatus.Paid));
		cache.AddOrUpdate(NewOrder("ORD-006", 42, OrderStatus.Paid));
		cache.AddOrUpdate(NewOrder("ORD-007", 7));
		cache.AddOrUpdate(NewOrder("ORD-001", 42));                        // Paid → Pending: appended to the Pending bucket
		return cache;
	}

	// Customer 42 in insertion order: 001 Paid, 002 Pending, 003 Pending, 004 Paid; CreatedAt grows with the id.
	private static OrderCache SortFixture() {
		var cache = new OrderCache();
		cache.AddOrUpdate(NewOrder("ORD-001", 42, OrderStatus.Paid, createdAt: 100));
		cache.AddOrUpdate(NewOrder("ORD-002", 42, createdAt: 200));
		cache.AddOrUpdate(NewOrder("ORD-003", 42, createdAt: 300));
		cache.AddOrUpdate(NewOrder("ORD-004", 42, OrderStatus.Paid, createdAt: 400));
		return cache;
	}

	private static string[] Ids(QueryResults<Order> rows) => rows.Select(static o => o.OrderId).ToArray();

	private static Order NewOrder(string orderId, int customerId, OrderStatus status = OrderStatus.Pending, long createdAt = 0) => new() {
		OrderId = orderId,
		CustomerId = customerId,
		CreatedAtUnixMs = createdAt,
		Status = status,
		Total = 99.99m,
	};

	private static OrderLine NewLine(string orderId, int line) => new() {
		LineId = $"{orderId}/{line}",
		OrderId = orderId,
		ProductId = line,
		Quantity = 1,
		UnitPrice = 10m,
	};

	#region by-status-comparer
	// Pending before Paid, nothing else: every row of one status compares equal.
	private readonly struct ByStatus : IComparer<Order> {
		public int Compare(Order? x, Order? y) => x!.Status.CompareTo(y!.Status);
	}
	#endregion
}
