// docs: every code block of www/docs/articles/getting-started/quick-start.md. The entity itself is
// Models/Order.cs (#order-entity). Each #region is one block the article pulls in verbatim.
namespace Prague.Samples.GettingStarted;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Prague.Core;
using Prague.Kafka;
using Prague.Samples.Models;

[TestFixture]
public class QuickStart {
	[Test]
	public void CreateCacheStandalone() {
		#region create-cache-standalone
		var cache = new OrderCache();
		#endregion

		Assert.That(cache.Query().Count(), Is.Zero);
	}

	[Test]
	public void CreateCacheThroughRegistry() {
		#region create-cache-registry
		var registry = new DataCacheRegistryBuilder()
			.Register<OrderCache>()
			.Build();
		var cache = registry.GetCache<OrderCache>();
		#endregion

		// Registering OrderCache pulls in the caches its foreign keys point at and wires the joins.
		Assert.That(cache, Is.Not.Null);
		Assert.That(registry.GetCache<CustomerCache>(), Is.Not.Null);
		Assert.That(registry.GetGlobalIndex<CustomerActivityIndex>(), Is.Not.Null);
	}

	[Test]
	public void CreateCacheFromKafkaThroughDi() {
		var services = new ServiceCollection();
		services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());   // a host registers this for you

		#region create-cache-kafka
		services.AddKafkaCaches(o => o.BootstrapServers = "kafka:9092",
			builder => builder.AddCache<OrderCache, string, Order>());

		var serviceProvider = services.BuildServiceProvider();   // in an app: the host's provider
		var cache = serviceProvider.GetRequiredService<OrderCache>();
		#endregion

		// Nothing has been consumed yet (no broker was contacted); the cache exists and is empty.
		Assert.That(cache.Query().Count(), Is.Zero);
		Assert.That(serviceProvider.GetRequiredService<OrderCache>(), Is.SameAs(cache));
		serviceProvider.Dispose();
	}

	[Test]
	public void WriteAndQuery() {
		var cache = new OrderCache();

		#region write-and-query
		cache.AddOrUpdate(new Order {                       // timestamp = now; AddOrUpdate(order, timestampMs) to set it
			OrderId = "ORD-001",
			CustomerId = 42,
			CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			Status = OrderStatus.Pending,
			Total = 99.99m,
		});

		var yesterdayUnixMs = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();

		using var orders = cache.Query()
			.WithCustomerId(42)
			.WithCreatedAtUnixMs(q => q.Gte(yesterdayUnixMs))
			.ExecutePooled();

		foreach (var order in orders)
			Console.WriteLine($"{order.OrderId} {order.Total}");
		#endregion

		Assert.That(orders.Count, Is.EqualTo(1));
		Assert.That(orders[0].OrderId, Is.EqualTo("ORD-001"));
		Assert.That(orders[0].Total, Is.EqualTo(99.99m));
	}

	[Test]
	public void IndexLanesIntersect() {
		var cache = new OrderCache();
		var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		cache.AddOrUpdate(NewOrder("ORD-001", 42, now));
		cache.AddOrUpdate(NewOrder("ORD-002", 42, now - 3 * DayMs));   // right customer, too old
		cache.AddOrUpdate(NewOrder("ORD-003", 7, now));                // recent, other customer

		var yesterdayUnixMs = now - DayMs;
		using var orders = cache.Query()
			.WithCustomerId(42)
			.WithCreatedAtUnixMs(q => q.Gte(yesterdayUnixMs))
			.ExecutePooled();

		Assert.That(orders.Count, Is.EqualTo(1));
		Assert.That(orders[0].OrderId, Is.EqualTo("ORD-001"));

		// An empty first lane short-circuits: nothing to intersect, nothing to scan.
		Assert.That(cache.Query().WithCustomerId(999).WithCreatedAtUnixMs(q => q.Gte(0)).Count(), Is.Zero);
	}

	[Test]
	public void ExecuteAllocatesAndNeedsNoDispose() {
		var cache = new OrderCache();
		cache.AddOrUpdate(NewOrder("ORD-001", 42, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

		var orders = cache.Query().WithCustomerId(42).Execute();

		Assert.That(orders.Count, Is.EqualTo(1));
	}

	[Test]
	public void DirectIndexAccess() {
		var cache = new OrderCache();
		var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		cache.AddOrUpdate(NewOrder("ORD-001", 42, now));
		cache.AddOrUpdate(NewOrder("ORD-002", 42, now - 3 * DayMs));
		cache.AddOrUpdate(NewOrder("ORD-003", 7, now));
		var yesterdayUnixMs = now - DayMs;

		#region direct-index-access
		var customerOrderIds = cache.CustomerIdIndex.GetValues(42);                   // Many  → keys
		var recentOrderIds = cache.CreatedAtUnixMsIndex.GetValuesGte(yesterdayUnixMs); // Range → keys

		if (cache.Cache.TryGet("ORD-001", out var order))                             // by primary key
			Console.WriteLine(order.Total);
		#endregion

		Assert.That(customerOrderIds, Is.EquivalentTo(new[] { "ORD-001", "ORD-002" }));
		Assert.That(recentOrderIds, Is.EquivalentTo(new[] { "ORD-001", "ORD-003" }));
		Assert.That(order, Is.Not.Null);
		Assert.That(order!.OrderId, Is.EqualTo("ORD-001"));
	}

	[Test]
	public void SameValueIsANoOp() {
		var cache = new OrderCache();
		var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		cache.AddOrUpdate(NewOrder("ORD-001", 42, createdAt));

		#region same-value-is-a-no-op
		var same = new Order {                              // equal field for field to what is stored
			OrderId = "ORD-001",
			CustomerId = 42,
			CreatedAtUnixMs = createdAt,
			Status = OrderStatus.Pending,
			Total = 99.99m,
		};

		var changed = cache.AddOrUpdate(same, out var previous);   // false: nothing swapped, previous is null
		#endregion

		Assert.That(changed, Is.False);
		Assert.That(previous, Is.Null);

		// A different value is swapped in and the old one is handed back.
		changed = cache.AddOrUpdate(NewOrder("ORD-001", 42, createdAt, total: 150m), out previous);

		Assert.That(changed, Is.True);
		Assert.That(previous, Is.Not.Null);
		Assert.That(previous!.Total, Is.EqualTo(99.99m));
		Assert.That(cache.Cache.TryGet("ORD-001", out var current) && current.Total == 150m);
	}

	private const long DayMs = 24 * 60 * 60 * 1000L;

	private static Order NewOrder(string orderId, int customerId, long createdAtUnixMs, decimal total = 99.99m) => new() {
		OrderId = orderId,
		CustomerId = customerId,
		CreatedAtUnixMs = createdAtUnixMs,
		Status = OrderStatus.Pending,
		Total = total,
	};
}
