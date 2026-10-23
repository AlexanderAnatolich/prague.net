// docs: every code block of www/docs/articles/core-concepts/index-types.md. The declarations live on the
// models (Models/Order.cs, Customer.cs, Product.cs, ShipmentLeg.cs, CarrierTrackingEvent.cs); each
// #region here is the query or direct read an index enables.
namespace Prague.Samples.Concepts;

using NUnit.Framework;
using Prague.Core;
using Prague.Samples.Models;

[TestFixture]
public class IndexTypes {
	private const long DayMs = 24 * 60 * 60 * 1000L;
	private const long Now = 1_760_000_000_000L;   // a fixed clock keeps the assertions deterministic

	private OrderCache _orders = null!;
	private CustomerCache _customers = null!;
	private ProductCache _products = null!;

	[SetUp]
	public void SetUp() {
		_orders = new OrderCache();
		_orders.AddOrUpdate(NewOrder("ORD-001", 42, Now, OrderStatus.Paid, settledAtUnixMs: Now));
		_orders.AddOrUpdate(NewOrder("ORD-002", 42, Now - 3 * DayMs, OrderStatus.Pending));
		_orders.AddOrUpdate(NewOrder("ORD-003", 7, Now - DayMs, OrderStatus.Shipped, settledAtUnixMs: Now));

		_customers = new CustomerCache();
		_customers.AddOrUpdate(NewCustomer(42, "alice@example.com", "US", CustomerTier.Gold));
		_customers.AddOrUpdate(NewCustomer(7, "bob@example.com", "GB", CustomerTier.Standard));
		_customers.AddOrUpdate(NewCustomer(9, "chen@example.com", "US", CustomerTier.Standard));

		_products = new ProductCache();
		_products.AddOrUpdate(NewProduct(1, "SKU-1", categoryId: 3, "Acme", 9.99m, stock: 5, published: true));
		_products.AddOrUpdate(NewProduct(2, "SKU-2", categoryId: 3, "Acme", 19.99m, stock: 0, published: true));
		_products.AddOrUpdate(NewProduct(3, "SKU-3", categoryId: 4, "Globex", 4.99m, stock: 12, published: false, discontinuedAtUnixMs: Now));
	}

	[Test]
	public void PrimaryKeyLookup() {
		#region primary-key-lookup
		using var byProperty = _orders.Query().WithOrderId("ORD-001").ExecutePooled();   // With{KeyProperty}
		using var byKey = _orders.Query().WithKey("ORD-001").ExecutePooled();            // the same lane under its generic name
		#endregion

		Assert.That(byProperty.Count, Is.EqualTo(1));
		Assert.That(byKey.Count, Is.EqualTo(1));
		Assert.That(byKey[0].OrderId, Is.EqualTo("ORD-001"));
	}

	[Test]
	public void SecondaryIndexLanes() {
		var since = Now - 2 * DayMs;

		#region secondary-index-lanes
		using var alice = _customers.Query().WithEmail("alice@example.com").ExecutePooled();        // Unique: at most one row
		using var ofCustomer = _orders.Query().WithCustomerId(42).ExecutePooled();                 // Many: every row with the value
		using var recent = _orders.Query().WithCreatedAtUnixMs(q => q.Gte(since)).ExecutePooled(); // Range: ordered walk from the bound
		#endregion

		Assert.That(alice.Count, Is.EqualTo(1));
		Assert.That(alice[0].Id, Is.EqualTo(42));
		Assert.That(Ids(ofCustomer), Is.EquivalentTo(new[] { "ORD-001", "ORD-002" }));
		Assert.That(Ids(recent), Is.EquivalentTo(new[] { "ORD-001", "ORD-003" }));
	}

	[Test]
	public void MultiValueLaneIsAnOr() {
		#region multi-value-lane
		var emails = new[] { "alice@example.com", "bob@example.com" };
		using var byArray = _customers.Query().WithEmail(emails).ExecutePooled();                            // array or ReadOnlySpan<T>: OR within the lane
		using var byList = _customers.Query().WithCountry(new List<string> { "US", "GB" }).ExecutePooled();

		int? customerId = null;
		using var unfiltered = _orders.Query().WithCustomerId(customerId).ExecutePooled();                 // null skips the lane entirely
		#endregion

		Assert.That(byArray.Count, Is.EqualTo(2));
		Assert.That(byList.Count, Is.EqualTo(3));
		Assert.That(unfiltered.Count, Is.EqualTo(3));
	}

	[Test]
	public void RangeSubBuilder() {
		var twoDaysAgo = Now - 2 * DayMs;

		#region range-sub-builder
		using var since = _orders.Query().WithCreatedAtUnixMs(q => q.Gte(twoDaysAgo)).ExecutePooled();            // open on the right
		using var window = _orders.Query().WithCreatedAtUnixMs(q => q.Gte(twoDaysAgo).Lt(Now)).ExecutePooled();   // [twoDaysAgo, Now)
		using var before = _orders.Query().WithCreatedAtUnixMs(q => q.Lt(twoDaysAgo)).ExecutePooled();            // open on the left
		#endregion

		Assert.That(Ids(since), Is.EquivalentTo(new[] { "ORD-001", "ORD-003" }));
		Assert.That(Ids(window), Is.EquivalentTo(new[] { "ORD-003" }));
		Assert.That(Ids(before), Is.EquivalentTo(new[] { "ORD-002" }));
	}

	[Test]
	public void DirectIndexReads() {
		#region direct-index-reads
		if (_customers.EmailIndex.TryGetValue("alice@example.com", out var aliceId))     // Unique → the primary key
			Console.WriteLine(aliceId);

		var ordersOf42 = _orders.CustomerIdIndex.GetValues(42);                          // Many → primary keys
		var lastTwoDays = _orders.CreatedAtUnixMsIndex.GetValuesGte(Now - 2 * DayMs);    // Range → primary keys, ordered by the index key
		var inWindow = _orders.CreatedAtUnixMsIndex.GetValuesBetween(Now - 2 * DayMs, Now);   // both bounds inclusive
		#endregion

		Assert.That(aliceId, Is.EqualTo(42));
		Assert.That(ordersOf42, Is.EquivalentTo(new[] { "ORD-001", "ORD-002" }));
		Assert.That(lastTwoDays, Is.EqualTo(new[] { "ORD-003", "ORD-001" }));
		Assert.That(inWindow, Is.EquivalentTo(new[] { "ORD-003", "ORD-001" }));
	}

	[Test]
	public void KeySetLanes() {
		var legs = new ShipmentLegCache();
		legs.AddOrUpdate(new ShipmentLeg { LegId = "SHP-1/1", Hop = ("SHP-1", 1), FromHub = "PRG", ToHub = "FRA", ArrivedAtUnixMs = Now });
		legs.AddOrUpdate(new ShipmentLeg { LegId = "SHP-1/2", Hop = ("SHP-1", 2), FromHub = "FRA", ToHub = "LHR" });

		#region key-set-lanes
		using var settled = _orders.Query().WithSettledAtUnixMs().ExecutePooled();       // [DataCacheHasValueIndex] on long?: SettledAtUnixMs != null
		using var published = _products.Query().WithIsPublished().ExecutePooled();       // [DataCacheHasValueIndex] on bool: IsPublished == true
		using var inStock = _products.Query().WithInStock().ExecutePooled();             // [DataCacheValueIndex(GreaterThan, 0, IndexName = "InStock")]: Stock > 0
		using var inFlight = legs.Query().WithoutArrivedAtUnixMs().ExecutePooled();      // [DataCacheHasNotValueIndex]: ArrivedAtUnixMs == null
		#endregion

		Assert.That(Ids(settled), Is.EquivalentTo(new[] { "ORD-001", "ORD-003" }));
		Assert.That(published.Count, Is.EqualTo(2));
		Assert.That(inStock.Count, Is.EqualTo(2));
		Assert.That(inFlight.Count, Is.EqualTo(1));
		Assert.That(inFlight[0].LegId, Is.EqualTo("SHP-1/2"));
	}

	[Test]
	public void SortComparers() {
		#region sort-comparers
		using var newestFirst = _orders.Query().WithCustomerId(42).Sort(OrderCache.ByCreatedAtComparer).ExecutePooled();
		using var cheapest = _products.Query().WithCategoryId(3).SortBounded(ProductCache.ByPriceComparer).ExecutePooled(skip: 0, take: 1);
		#endregion

		Assert.That(Ids(newestFirst), Is.EqualTo(new[] { "ORD-001", "ORD-002" }));
		Assert.That(cheapest.Count, Is.EqualTo(1));
		Assert.That(cheapest[0].Id, Is.EqualTo(1));
	}

	[Test]
	public void ExternalTypeQuery() {
		#region external-type-query
		var events = new CarrierTrackingEventCache();
		events.AddOrUpdate(new CarrierTrackingEvent { EventId = "evt-1", ShipmentId = "SHP-1", Status = "IN_TRANSIT" });
		events.AddOrUpdate(new CarrierTrackingEvent { EventId = "evt-2", ShipmentId = "SHP-1", Status = "DELIVERED", DeliveredAtUnixMs = Now });
		events.AddOrUpdate(new CarrierTrackingEvent { EventId = "evt-3", ShipmentId = "SHP-2", Status = "IN_TRANSIT" });

		using var delivered = events.Query().WithShipmentId("SHP-1").WithStatus("DELIVERED").ExecutePooled();
		#endregion

		Assert.That(delivered.Count, Is.EqualTo(1));
		Assert.That(delivered[0].EventId, Is.EqualTo("evt-2"));
	}

	private static string[] Ids(QueryResults<Order> orders) {
		var ids = new string[orders.Count];
		for (var i = 0; i < ids.Length; i++)
			ids[i] = orders[i].OrderId;
		return ids;
	}

	private static Order NewOrder(string orderId, int customerId, long createdAtUnixMs, OrderStatus status, long? settledAtUnixMs = null) => new() {
		OrderId = orderId,
		CustomerId = customerId,
		CreatedAtUnixMs = createdAtUnixMs,
		Status = status,
		SettledAtUnixMs = settledAtUnixMs,
		Total = 99.99m,
	};

	private static Customer NewCustomer(int id, string email, string country, CustomerTier tier) => new() {
		Id = id,
		Email = email,
		Country = country,
		Tier = tier,
		Name = email[..email.IndexOf('@')],
	};

	private static Product NewProduct(int id, string sku, int categoryId, string brand, decimal price, int stock, bool published, long? discontinuedAtUnixMs = null) => new() {
		Id = id,
		Sku = sku,
		CategoryId = categoryId,
		Brand = brand,
		Price = price,
		Stock = stock,
		IsPublished = published,
		DiscontinuedAtUnixMs = discontinuedAtUnixMs,
		Name = sku,
	};
}
