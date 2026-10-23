// docs: every code block of www/docs/articles/how-to/react-to-changes.md — an ICacheAfterHandler over
// OrderCache, how each UpdateType reaches it, and how generated equality decides Same vs Update.
namespace Prague.Samples.HowTo;

using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Prague.Kafka;
using Prague.Samples.Models;

#region react-handler
public sealed class OrderProjector : ICacheAfterHandler<string, Order> {
	private readonly Dictionary<int, decimal> _openTotalByCustomer = new();   // only this worker writes it

	public IReadOnlyDictionary<int, decimal> OpenTotalByCustomer => _openTotalByCustomer;

	public ValueTask Handle(UpdateType updateType, string key, Order? newValue, Order? oldValue) {
		switch (updateType) {
			case UpdateType.Add:                                  // oldValue is null
				Apply(newValue!, +1);
				break;
			case UpdateType.Update:                               // both values set
				Apply(oldValue!, -1);
				Apply(newValue!, +1);
				break;
			case UpdateType.Delete:                               // newValue is null; oldValue is what was removed
				Apply(oldValue!, -1);
				break;
			case UpdateType.Same:                                 // a re-emit of the resident value: nothing to do
			case UpdateType.Filtered:                             // carries NO key and NO values — never read `key` here
				break;
		}

		return ValueTask.CompletedTask;                           // keep it short: it runs on the cache's live worker
	}

	private void Apply(Order order, int sign) {
		if (order.Status is not (OrderStatus.Pending or OrderStatus.Paid))
			return;
		_openTotalByCustomer[order.CustomerId] = _openTotalByCustomer.GetValueOrDefault(order.CustomerId) + sign * order.Total;
	}
}
#endregion

public static class ReactToChangesWiring {
	#region react-register
	public static void Register(KafkaCacheHandlersBuilder b) {
		b.AddCache<OrderCache, string, Order>()
			.WithAfterHandler<OrderProjector>()                 // singleton ICacheAfterHandler<string, Order>
			.WithAfterHandler(sp => new OrderAuditLog(sp.GetRequiredService<TimeProvider>()));   // factory form
	}
	#endregion
}

public sealed class OrderAuditLog(TimeProvider clock) : ICacheAfterHandler<string, Order> {
	public List<string> Lines { get; } = new();

	public ValueTask Handle(UpdateType updateType, string key, Order? newValue, Order? oldValue) {
		if (updateType is not UpdateType.Filtered)
			Lines.Add($"{clock.GetUtcNow():O} {updateType} {key}");
		return ValueTask.CompletedTask;
	}
}

[TestFixture]
public class ReactToChanges {
	[Test]
	public async Task ProjectorFollowsTheUpdateTypes() {
		#region react-handler-test
		var projector = new OrderProjector();
		var pending = new Order { OrderId = "ORD-001", CustomerId = 42, CreatedAtUnixMs = 1, Status = OrderStatus.Pending, Total = 100m };
		var paid = new Order { OrderId = "ORD-001", CustomerId = 42, CreatedAtUnixMs = 1, Status = OrderStatus.Paid, Total = 100m };
		var shipped = new Order { OrderId = "ORD-001", CustomerId = 42, CreatedAtUnixMs = 1, Status = OrderStatus.Shipped, Total = 100m };

		await projector.Handle(UpdateType.Add, "ORD-001", pending, null);        // +100
		await projector.Handle(UpdateType.Same, "ORD-001", pending, null);       // re-emit: no change
		await projector.Handle(UpdateType.Update, "ORD-001", paid, pending);     // still open: -100 +100
		await projector.Handle(UpdateType.Update, "ORD-001", shipped, paid);     // leaves the open set: -100
		await projector.Handle(UpdateType.Filtered, default!, null, null);       // what a filtered message looks like
		#endregion

		Assert.That(projector.OpenTotalByCustomer[42], Is.Zero);
	}

	[Test]
	public void StructurallyEqualWritesAreSame() {
		var products = new ProductCache();

		#region react-same-vs-update
		var first = NewProduct(name: "Widget");
		products.AddOrUpdate(first);

		var reEmit = NewProduct(name: "Widget");                               // a new instance, same content (a replayed message)
		var changed = products.AddOrUpdate(reEmit, out var previous);          // false → the live phase reports Same
		var renamed = products.AddOrUpdate(NewProduct(name: "Gadget"), out previous);   // true → Update, previous = the Widget

		var equal = first.CacheEquals(reEmit);                                 // the generated deep equality, callable directly
		#endregion

		Assert.That(changed, Is.False);
		Assert.That(equal, Is.True);
		Assert.That(renamed, Is.True);
		Assert.That(previous!.Name, Is.EqualTo("Widget"));
		Assert.That(products.Cache.TryGet(1, out var current) && ReferenceEquals(current, previous) is false && current.Name == "Gadget");
	}

	[Test]
	public async Task FactoryRegisteredHandlerResolves() {
		var services = new ServiceCollection();
		services.AddSingleton(TimeProvider.System);
		services.AddSingleton<ICacheAfterHandler<string, Order>>(sp => new OrderAuditLog(sp.GetRequiredService<TimeProvider>()));
		using var provider = services.BuildServiceProvider();

		var handler = provider.GetRequiredService<ICacheAfterHandler<string, Order>>();
		await handler.Handle(UpdateType.Delete, "ORD-001", null, NewOrder());

		Assert.That(((OrderAuditLog)handler).Lines.Single(), Does.EndWith("Delete ORD-001"));
	}

	private static Product NewProduct(string name) => new() {
		Id = 1,
		Sku = "W-1",
		CategoryId = 10,
		Brand = "Acme",
		Price = 9.99m,
		Stock = 5,
		IsPublished = true,
		Name = name,
		LastSeenAtUnixMs = 1_000,
	};

	private static Order NewOrder() => new() { OrderId = "ORD-001", CustomerId = 42, CreatedAtUnixMs = 1, Status = OrderStatus.Paid, Total = 1m };
}
