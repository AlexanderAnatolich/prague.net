// docs: every code block of www/docs/articles/how-to/incremental-sync.md — serve "what changed since my last
// watermark" from CustomerActivityIndex, the global last-update index that Customer (group key = its primary
// key) and Order (group key = CustomerId) both report into.
namespace Prague.Samples.HowTo;

using NUnit.Framework;
using Prague.Core;
using Prague.Samples.Models;

#region sync-response
public sealed record CustomerChanges(Customer Customer, Order[] Orders);

public sealed record SyncResponse(CustomerChanges[] Changed, long Watermark);
#endregion

public sealed class CustomerSync {
	private readonly CustomerCache _customers;
	private readonly OrderCache _orders;
	private readonly CustomerActivityIndex _activity;

	public CustomerSync(DataCacheRegistry registry) {
		_customers = registry.GetCache<CustomerCache>();
		_orders = registry.GetCache<OrderCache>();
		_activity = registry.GetGlobalIndex<CustomerActivityIndex>();   // from DI: GetRequiredService<CustomerActivityIndex>()
	}

	#region sync-cross-cache
	public SyncResponse ChangesSince(long watermarkMs) {
		// Which customers changed — through their own row OR through any of their orders. Strictly after the watermark.
		var changedIds = _activity.Index.GetValuesGt(watermarkMs);
		if (changedIds.Count == 0)
			return new SyncResponse([], watermarkMs);

		var ids = changedIds.ToArray();
		var changes = new List<CustomerChanges>(ids.Length);
		foreach (var customerId in ids) {
			if (!_customers.Cache.TryGet(customerId, out var customer))
				continue;                                           // the customer row itself is gone: see "Deletes"
			var orders = _orders.Query().WithCustomerId(customerId).Execute();   // allocating: the array goes into the DTO
			changes.Add(new CustomerChanges(customer, orders.ToArray()));
		}

		// The watermark to hand back: the newest timestamp among the groups we actually returned.
		_activity.TryGetMax(ids, out var newWatermark);
		return new SyncResponse(changes.ToArray(), newWatermark);
	}
	#endregion
}

[TestFixture]
public class IncrementalSync {
	private const long T0 = 1_700_000_000_000L;

	private DataCacheRegistry _registry = null!;
	private CustomerCache _customers = null!;
	private OrderCache _orders = null!;
	private CustomerActivityIndex _activity = null!;

	[SetUp]
	public void SetUp() {
		_registry = new DataCacheRegistryBuilder().Register<OrderCache>().Build();   // Customer comes in through the FK
		_customers = _registry.GetCache<CustomerCache>();
		_orders = _registry.GetCache<OrderCache>();
		_activity = _registry.GetGlobalIndex<CustomerActivityIndex>();

		// Explicit timestamps: on the Kafka path this is the message timestamp.
		_customers.AddOrUpdate(new Customer { Id = 42, Email = "ada@example.com", Country = "GB", Tier = CustomerTier.Gold, Name = "Ada" }, T0 + 1_000);
		_customers.AddOrUpdate(new Customer { Id = 7, Email = "bob@example.com", Country = "US", Tier = CustomerTier.Standard, Name = "Bob" }, T0 + 2_000);
		_orders.AddOrUpdate(NewOrder("ORD-001", 42, 120m), T0 + 3_000);
		_orders.AddOrUpdate(NewOrder("ORD-002", 7, 80m), T0 + 4_000);
	}

	[Test]
	public void UpdatedAfterOnThePrimaryKeyCache() {
		var customers = _customers;
		var lastSyncMs = T0 + 3_000;                            // Ada's row is T0+1000, but ORD-001 (hers) bumped her to T0+3000

		#region sync-updated-after
		using var changed = customers.Query()
			.UpdatedAfter(lastSyncMs, out var newWatermarkMs)   // strictly greater than lastSyncMs
			.ExecutePooled();

		// ... send `changed` to the client together with newWatermarkMs; it echoes it back next time.
		#endregion

		Assert.That(changed.Select(c => c.Id), Is.EqualTo(new[] { 7 }));           // Bob: ORD-002 at T0+4000; Ada at T0+3000 is not "after"
		Assert.That(newWatermarkMs, Is.EqualTo(T0 + 4_000));

		using var nothingNew = customers.Query().UpdatedAfter(newWatermarkMs).ExecutePooled();
		Assert.That(nothingNew.Count, Is.Zero);
	}

	[Test]
	public void AnOrderWriteBumpsItsCustomer() {
		var activity = _activity;
		var orders = _orders;

		#region sync-order-write-bumps-customer
		orders.AddOrUpdate(NewOrder("ORD-003", 42, 5m), T0 + 10_000);   // Ada's row did not change, her activity did

		activity.Index.TryGetLastUpdated(42, out var adaActivityMs);   // T0 + 10_000
		var changedCustomers = activity.Index.GetValuesGt(T0 + 4_000);  // [42]
		#endregion

		Assert.That(adaActivityMs, Is.EqualTo(T0 + 10_000));
		Assert.That(changedCustomers, Is.EqualTo(new[] { 42 }));

		// Customer.UpdatedAfter sees it too: the index is shared, the customer row is read from its own cache.
		using var viaQuery = _customers.Query().UpdatedAfter(T0 + 4_000).ExecutePooled();
		Assert.That(viaQuery.Single().Name, Is.EqualTo("Ada"));
	}

	[Test]
	public void CrossCacheSyncRoundTrips() {
		var sync = new CustomerSync(_registry);

		var full = sync.ChangesSince(0);                                   // first call: everything
		Assert.That(full.Changed.Select(c => c.Customer.Id), Is.EquivalentTo(new[] { 42, 7 }));
		Assert.That(full.Watermark, Is.EqualTo(T0 + 4_000));

		var idle = sync.ChangesSince(full.Watermark);                      // nothing happened
		Assert.That(idle.Changed, Is.Empty);
		Assert.That(idle.Watermark, Is.EqualTo(full.Watermark));           // unchanged: the client keeps it

		_orders.AddOrUpdate(NewOrder("ORD-003", 42, 5m), T0 + 10_000);
		var delta = sync.ChangesSince(full.Watermark);                     // only Ada, with all her orders
		Assert.That(delta.Changed.Single().Customer.Name, Is.EqualTo("Ada"));
		Assert.That(delta.Changed.Single().Orders.Select(o => o.OrderId), Is.EquivalentTo(new[] { "ORD-001", "ORD-003" }));
		Assert.That(delta.Watermark, Is.EqualTo(T0 + 10_000));
	}

	[Test]
	public void Deletes() {
		var orders = _orders;
		var customers = _customers;
		var activity = _activity;
		orders.AddOrUpdate(NewOrder("ORD-003", 42, 5m), T0 + 5_000);

		#region sync-deletes
		// Deleting one of several entities in a group bumps the group to the delete time: the client re-reads
		// customer 42 and sees that ORD-003 is gone from the list.
		orders.Cache.Remove("ORD-003", T0 + 20_000);
		var bumped = activity.Index.GetValuesGt(T0 + 5_000);               // [42]

		// Deleting the LAST entity of a group drops the group from the index: nothing is "after" any watermark.
		orders.Cache.Remove("ORD-002", T0 + 21_000);                        // Bob's only order
		customers.Cache.Remove(7, T0 + 22_000);                             // and Bob himself
		var bobStillTracked = activity.Index.TryGetLastUpdated(7, out _);  // false
		#endregion

		Assert.That(bumped, Is.EqualTo(new[] { 42 }));
		Assert.That(bobStillTracked, Is.False);
		Assert.That(activity.GetEntitiesCount(7), Is.Zero);
		Assert.That(activity.Index.GetValuesGt(T0 + 20_000), Is.Empty);    // Bob's removal is invisible here
	}

	[Test]
	public void PreparedWatermarkQuery() {
		var customers = _customers;

		#region sync-prepared
		var changedSince = customers.Prepare<long>()
			.UpdatedAfter(static since => since)                // parameterized twin; also (from, to) and DateTimeOffset forms
			.BuildFrozen();

		using var rows = changedSince.ExecutePooled(T0 + 3_000);
		#endregion

		Assert.That(rows.Select(c => c.Id), Is.EqualTo(new[] { 7 }));
	}

	private static Order NewOrder(string orderId, int customerId, decimal total) => new() {
		OrderId = orderId,
		CustomerId = customerId,
		CreatedAtUnixMs = T0,
		Status = OrderStatus.Paid,
		Total = total,
	};
}
