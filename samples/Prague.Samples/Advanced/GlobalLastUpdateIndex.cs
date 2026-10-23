// docs: every code block of www/docs/articles/advanced/global-last-update-index.md. The index itself is
// Models/CustomerActivityIndex.cs; Customer reports into it under its primary key, Order under its
// CustomerId foreign key (Models/Customer.cs, Models/Order.cs).
namespace Prague.Samples.Advanced;

using NUnit.Framework;
using Prague.Core;
using Prague.Samples.Models;

[TestFixture]
public class GlobalLastUpdateIndex {
	private const long T0 = 1_760_000_000_000L;

	private DataCacheRegistry _registry = null!;
	private CustomerCache _customers = null!;
	private OrderCache _orders = null!;

	[SetUp]
	public void SetUp() {
		_registry = new DataCacheRegistryBuilder()
			.Register<CustomerCache>()
			.Register<OrderCache>()
			.Register<OrderLineCache>()
			.Register<ShipmentCache>()
			.Register<ProductCache>()
			.Build();
		_customers = _registry.GetCache<CustomerCache>();
		_orders = _registry.GetCache<OrderCache>();

		// AddOrUpdate(value, timestampMs) is the timestamp the index records; the Kafka consumer passes the message timestamp.
		_customers.AddOrUpdate(NewCustomer(42), T0 + 1_000);
		_customers.AddOrUpdate(NewCustomer(7), T0 + 2_000);
		_customers.AddOrUpdate(NewCustomer(9), T0 + 3_000);
		_orders.AddOrUpdate(NewOrder("ORD-001", 42), T0 + 5_000);     // an order write stamps customer 42 too
		_orders.AddOrUpdate(NewOrder("ORD-002", 7), T0 + 1_500);
	}

	[Test]
	public void ResolvingTheIndex() {
		#region resolve-index
		var index = _registry.GetGlobalIndex<CustomerActivityIndex>();   // from DI: serviceProvider.GetRequiredService<CustomerActivityIndex>()
		#endregion

		Assert.That(index, Is.Not.Null);
		Assert.That(index.TryGetMax(out var newest, out var who) && newest == T0 + 5_000 && who == 42);
	}

	[Test]
	public void UpdatedAfterOnThePrimaryKeyCache() {
		var lastSyncUnixMs = T0 + 2_500;

		#region updated-after
		using var changed = _customers.Query()
			.UpdatedAfter(lastSyncUnixMs)                               // strictly greater than lastSyncUnixMs
			.ExecutePooled();
		#endregion

		Assert.That(changed.Map(c => c.Id).ToArray(), Is.EquivalentTo(new[] { 9, 42 }), "42 through its order, 9 through its own write");
	}

	[Test]
	public void UpdatedAfterReportsTheWatermark() {
		var lastSyncUnixMs = T0 + 2_500;

		#region updated-after-watermark
		using var window = _customers.Query()
			.UpdatedAfter(lastSyncUnixMs, out var maxObservedUnixMs)    // highest timestamp among the rows returned
			.ExecutePooled();

		CommitSyncPoint(maxObservedUnixMs);                             // the safe watermark for the next call
		#endregion

		Assert.That(window.Count, Is.EqualTo(2));
		Assert.That(maxObservedUnixMs, Is.EqualTo(T0 + 5_000));
	}

	[Test]
	public void GroupKeyThatIsNotThePrimaryKey() {
		var index = _registry.GetGlobalIndex<CustomerActivityIndex>();
		var lastSyncUnixMs = T0 + 2_500;
		var touched = new List<string>();

		#region group-key-not-pk
		foreach (var customerId in index.Index.GetValuesGt(lastSyncUnixMs)) {        // which groups changed…
			using var orders = _orders.Query().WithCustomerId(customerId).ExecutePooled();   // …then each group's rows via its own index
			foreach (var order in orders)
				touched.Add(order.OrderId);
		}
		#endregion

		Assert.That(touched, Is.EquivalentTo(new[] { "ORD-001" }));
	}

	[Test]
	public void IndexSurface() {
		var index = _registry.GetGlobalIndex<CustomerActivityIndex>();

		#region index-surface
		var hasAny = index.TryGetMax(out var newestUnixMs, out var newestCustomerId);   // the most recent change and its group
		var oldest = index.TryGetMin(out var oldestUnixMs);
		var rowsOf42 = index.GetEntitiesCount(42);                                      // entities tracked under the group, across caches
		var since = index.Index.GetValuesBetween(T0 + 1_000, T0 + 2_000);               // long / DateTime / DateTimeOffset forms
		#endregion

		Assert.That(hasAny && oldest);
		Assert.That((newestUnixMs, newestCustomerId), Is.EqualTo((T0 + 5_000, 42)));
		Assert.That(oldestUnixMs, Is.EqualTo(T0 + 2_000), "7's latest change is its own write; 42 moved to T0+5000");
		Assert.That(rowsOf42, Is.EqualTo(2));
		Assert.That(since, Is.EquivalentTo(new[] { 7 }));
	}

	private static void CommitSyncPoint(long watermarkUnixMs) => Console.WriteLine($"synced up to {watermarkUnixMs}");

	private static Customer NewCustomer(int id) => new() {
		Id = id,
		Email = $"c{id}@example.com",
		Country = "US",
		Tier = CustomerTier.Standard,
		Name = $"Customer {id}",
	};

	private static Order NewOrder(string orderId, int customerId) => new() {
		OrderId = orderId,
		CustomerId = customerId,
		CreatedAtUnixMs = T0,
		Status = OrderStatus.Pending,
		Total = 10m,
	};
}
