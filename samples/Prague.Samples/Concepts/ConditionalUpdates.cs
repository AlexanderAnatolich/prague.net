// docs: every code block of www/docs/articles/core-concepts/conditional-updates.md — the out overload of
// AddOrUpdate, [DataCacheIgnoreEquality], generated CacheEquals, and an ICacheAfterHandler with its
// registration. The handler only ever fires on the Kafka live phase, so the registration test asserts
// the DI wiring, not a delivery.
namespace Prague.Samples.Concepts;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Prague.Core;
using Prague.Kafka;
using Prague.Samples.Models;

#region after-handler
public sealed class OrderProjector : ICacheAfterHandler<string, Order> {
	public ValueTask Handle(UpdateType updateType, string key, Order? newValue, Order? oldValue) {
		switch (updateType) {
			case UpdateType.Add:
			case UpdateType.Update:
				Project(newValue!);                   // oldValue is null on Add, the previous row on Update
				break;
			case UpdateType.Delete:
				Retract(key, oldValue!);              // newValue is null, oldValue is the removed row
				break;
			case UpdateType.Same:                     // a re-emit of the resident value: a keepalive at most
			case UpdateType.Filtered:                 // a message a filter dropped: carries no key and no values
				break;
		}

		return ValueTask.CompletedTask;
	}

	private static void Project(Order order) => Console.WriteLine($"upsert {order.OrderId}");

	private static void Retract(string key, Order previous) => Console.WriteLine($"delete {key} (was {previous.Total})");
}
#endregion

[TestFixture]
public class ConditionalUpdates {
	private const long Now = 1_760_000_000_000L;

	[Test]
	public void AddOrUpdateReportsWhetherAnythingChanged() {
		var cache = new OrderCache();
		var order = NewOrder("ORD-001", 99.99m);
		cache.AddOrUpdate(order);

		#region add-or-update-out
		var changed = cache.AddOrUpdate(NewOrder("ORD-001", 99.99m), out var previous);   // false: structurally equal → Same
		#endregion

		Assert.That(changed, Is.False);
		Assert.That(previous, Is.Null);
		Assert.That(cache.Cache.TryGet("ORD-001", out var resident) && ReferenceEquals(resident, order), "the resident instance was not swapped");

		changed = cache.AddOrUpdate(NewOrder("ORD-001", 150m), out previous);              // true: Update, previous is the old row

		Assert.That(changed, Is.True);
		Assert.That(previous, Is.SameAs(order));
	}

	[Test]
	public void IgnoredPropertiesDoNotMakeAnUpdate() {
		var products = new ProductCache();
		products.AddOrUpdate(NewProduct(lastSeenAtUnixMs: Now));

		#region ignore-equality
		var changed = products.AddOrUpdate(NewProduct(lastSeenAtUnixMs: Now + 60_000), out _);   // only [DataCacheIgnoreEquality] fields differ → Same
		#endregion

		Assert.That(changed, Is.False);
		Assert.That(products.Cache.TryGet(1, out var resident) && resident.LastSeenAtUnixMs == Now, "the earlier value stays resident");
	}

	[Test]
	public void CacheEqualsIsGeneratedDeepEquality() {
		#region cache-equals
		var a = NewOrder("ORD-001", 99.99m);
		var b = NewOrder("ORD-001", 99.99m);

		var structurallyEqual = a.CacheEquals(b);          // true: every property compared, nested objects and collections recursed
		var sameReference = a.Equals(b);                   // false: object.Equals is untouched
		#endregion

		Assert.That(structurallyEqual, Is.True);
		Assert.That(sameReference, Is.False);
		Assert.That(a.CacheGetHashCode(), Is.EqualTo(b.CacheGetHashCode()));
	}

	[Test]
	public void AfterHandlerRegistration() {
		var services = new ServiceCollection();
		services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

		#region after-handler-registration
		services.AddKafkaCaches(o => o.BootstrapServers = "kafka:9092",
			builder => builder.AddCache<OrderCache, string, Order>()
				.WithAfterHandler<OrderProjector>());
		#endregion

		using var provider = services.BuildServiceProvider();

		Assert.That(provider.GetServices<ICacheAfterHandler<string, Order>>().Single(), Is.InstanceOf<OrderProjector>());
	}

	private static Order NewOrder(string orderId, decimal total) => new() {
		OrderId = orderId,
		CustomerId = 42,
		CreatedAtUnixMs = Now,
		Status = OrderStatus.Pending,
		Total = total,
	};

	private static Product NewProduct(long lastSeenAtUnixMs) => new() {
		Id = 1,
		Sku = "SKU-1",
		CategoryId = 3,
		Brand = "Acme",
		Price = 9.99m,
		Stock = 5,
		IsPublished = true,
		Name = "Widget",
		LastSeenAtUnixMs = lastSeenAtUnixMs,
	};
}
