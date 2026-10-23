// docs: every code block of www/docs/articles/advanced/performance-tuning.md — pooled execution, holding
// rows across an await, index order, static join filters, span iteration, bounded paging, statistics.
namespace Prague.Samples.Advanced;

using System.Buffers;
using NUnit.Framework;
using Prague.Core;
using Prague.Samples.Models;

[TestFixture]
public class PerformanceTuning {
	private const long DayMs = 24 * 60 * 60 * 1000L;
	private const long Now = 1_760_000_000_000L;

	private DataCacheRegistry _registry = null!;
	private OrderCache _orders = null!;
	private CustomerCache _customers = null!;

	[SetUp]
	public void SetUp() {
		_registry = new DataCacheRegistryBuilder()
			.Register<OrderCache>()
			.Register<CustomerCache>()
			.Register<OrderLineCache>()
			.Register<ShipmentCache>()
			.Register<ProductCache>()
			.Build();
		_orders = _registry.GetCache<OrderCache>();
		_customers = _registry.GetCache<CustomerCache>();

		_customers.AddOrUpdate(new Customer { Id = 42, Email = "alice@example.com", Country = "US", Tier = CustomerTier.Gold, Name = "Alice" });
		_customers.AddOrUpdate(new Customer { Id = 7, Email = "bob@example.com", Country = "GB", Tier = CustomerTier.Standard, Name = "Bob" });
		for (var i = 1; i <= 30; i++)
			_orders.AddOrUpdate(NewOrder($"ORD-{i:D3}", i % 3 == 0 ? 7 : 42, Now - i * DayMs, total: i * 10m));
	}

	[Test]
	public void AlwaysUseExecutePooled() {
		#region execute-pooled
		using var results = _orders.Query().WithCustomerId(42).ExecutePooled();   // rented from ArrayPool; the using returns it
		#endregion

		Assert.That(results.Count, Is.EqualTo(20));
	}

	[Test]
	public async Task HoldingRowsAcrossAnAwait() {
		#region hold-across-await
		var snapshot = _orders.Query().WithCustomerId(42).ExecutePooled().ToArray();                       // copies out, disposes the pooled buffer

		var rented = _orders.Query().WithCustomerId(42).ExecutePooled().ToPooledArray(ArrayPool<Order>.Shared);   // hands the rows over in a buffer you own…
		try {
			await Task.Yield();
			Console.WriteLine(rented[0].OrderId);
		} finally {
			ArrayPool<Order>.Shared.Return(rented, clearArray: true);                                     // …and return
		}
		#endregion

		Assert.That(snapshot, Has.Length.EqualTo(20));
	}

	[Test]
	public void IndexOrderMattersOnTheEagerPath() {
		var since = Now - 10 * DayMs;

		#region index-order
		// Good — the Unique lane narrows to at most one candidate before the range walk.
		using var narrowFirst = _customers.Query()
			.WithEmail("alice@example.com")                   // Unique
			.WithCountry("US")                                // Many: probes one candidate
			.ExecutePooled();

		// Worse — the wide lane walks every US customer first, then intersects with the unique hit.
		using var wideFirst = _customers.Query()
			.WithCountry("US")                                // Many: every US customer
			.WithEmail("alice@example.com")                   // Unique
			.ExecutePooled();

		// Same idea on orders: a customer's rows first, the time window second.
		using var recent = _orders.Query()
			.WithCustomerId(42)
			.WithCreatedAtUnixMs(q => q.Gte(since))
			.ExecutePooled();
		#endregion

		Assert.That(narrowFirst.Count, Is.EqualTo(1));
		Assert.That(wideFirst.Count, Is.EqualTo(1));
		Assert.That(recent.Count, Is.EqualTo(7));
	}

	[Test]
	public void StaticLambdasInJoinFilters() {
		#region join-filter-static
		// Good — static lambda + argument: nothing captured, no closure allocated per execution.
		using var noCapture = _orders.Query()
			.JoinWithCustomer(static (q, tier) => q.WithTier(tier), CustomerTier.Gold)
			.ExecutePooled();

		// Worse — the lambda captures a local, so a closure is allocated every time the query runs.
		var tier = CustomerTier.Gold;
		using var capturing = _orders.Query()
			.JoinWithCustomer(q => q.WithTier(tier))
			.ExecutePooled();
		#endregion

		Assert.That(noCapture.Count, Is.EqualTo(30));
		Assert.That(noCapture.Map(r => r.Right is not null).ToArray().Count(x => x), Is.EqualTo(20));
		Assert.That(capturing.Count, Is.EqualTo(30));
	}

	[Test]
	public void IterateTheSpanInsteadOfCopying() {
		#region avoid-tolist
		using var orders = _orders.Query().WithCustomerId(42).ExecutePooled();
		decimal sum = 0;
		foreach (var o in orders.AsSpan())
			sum += o.Total;
		#endregion

		Assert.That(sum, Is.EqualTo(3000m));
	}

	[Test]
	public void PageWithSortBounded() {
		#region sort-bounded-page
		// Good — narrow to one customer's rows, then select a page of 5 from them.
		using var page = _orders.Query().WithCustomerId(42).SortBounded(OrderCache.ByCreatedAtComparer).ExecutePooled(skip: 0, take: 5);

		// Wasteful — a full sort of the whole cache, then a slice.
		using var sliced = _orders.Query().Sort(OrderCache.ByCreatedAtComparer).ExecutePooled(skip: 0, take: 5);
		#endregion

		Assert.That(page.Count, Is.EqualTo(5));
		Assert.That(page.TotalCount, Is.EqualTo(20));
		Assert.That(sliced.TotalCount, Is.EqualTo(30));
	}

	[Test]
	public void Statistics() {
		#region statistics
		var cache = new OrderCache(collectStatistics: true);   // the default constructor wires a no-op collector: counters stay 0
		cache.AddOrUpdate(NewOrder("ORD-001", 42, Now, 10m));
		cache.AddOrUpdate(NewOrder("ORD-002", 42, Now, 20m));

		var stats = cache.Statistics;
		Console.WriteLine($"size={stats.LiveSize} indices={stats.Indexes.Count}");          // Live*: read-through, no snapshot
		foreach (var (name, index) in stats.Indexes)
			Console.WriteLine($"  {name}: keys={index.LiveKeysSize} values={index.LiveValuesSize}");

		DataCacheStatisticsMarshall.TakeSnapshot(stats);                                     // periodic monitoring: copies Live* into Size / KeysSize / ValuesSize
		#endregion

		Assert.That(stats.LiveSize, Is.EqualTo(2u));
		Assert.That(stats.Size, Is.EqualTo(2u));
		Assert.That(stats.Indexes["CustomerIdIndex"].KeysSize, Is.EqualTo(1u));
		Assert.That(stats.Indexes["StatusIndex"].KeysSize, Is.EqualTo(1u));
		Assert.That(new OrderCache().Statistics.LiveSize, Is.Zero, "no-op collector");
	}

	private static Order NewOrder(string orderId, int customerId, long createdAtUnixMs, decimal total) => new() {
		OrderId = orderId,
		CustomerId = customerId,
		CreatedAtUnixMs = createdAtUnixMs,
		Status = OrderStatus.Pending,
		Total = total,
	};
}
