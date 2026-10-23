// docs: every code block of www/docs/articles/tutorial/build-an-order-service.md. The models are the shared
// ones in Models/. Steps that need a broker (AddKafkaCaches wiring, waiting for the load, health checks,
// Program.cs) are compiled-only methods; every step that runs without one is a [Test].
namespace Prague.Samples.Tutorial;

using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Prague.Core;
using Prague.Kafka;
using Prague.Kafka.Health;
using Prague.Samples.Models;

#region tutorial-shipment-notifier
public sealed class ShipmentNotifier : ICacheAfterHandler<string, Shipment> {
	private readonly List<string> _notified = new();

	public IReadOnlyList<string> Notified => _notified;

	public ValueTask Handle(UpdateType updateType, string key, Shipment? newValue, Shipment? oldValue) {
		// Live phase only: the initial replay of the topic never reaches this method.
		if (updateType is UpdateType.Add or UpdateType.Update)
			_notified.Add($"{newValue!.OrderId} shipped with {newValue.Carrier}");
		return ValueTask.CompletedTask;                         // no I/O here; this runs on the cache's live worker
	}
}
#endregion

#region tutorial-frozen-query
public sealed class CustomerOrdersPage {
	public readonly record struct Args(int CustomerId, OrderStatus Status);

	private readonly FrozenQuery<Args, Order> _page;

	public CustomerOrdersPage(OrderCache orders) {
		_page = orders.Prepare<Args>()
			.WithCustomerId(static a => a.CustomerId)          // parameterized lane
			.WithStatus(static a => a.Status)
			.SortBounded(OrderCache.ByCreatedAtComparer)        // newest first, OrderId breaks ties: a total order
			.BuildFrozen();                                     // planned once; the only allocation
	}

	public OrderDto[] Get(int customerId, OrderStatus status, int skip, int take) {
		using var rows = _page.ExecutePooled(new Args(customerId, status), skip, take);   // 0 B, any thread
		var dtos = new OrderDto[rows.Count];
		for (var i = 0; i < rows.Count; i++)
			dtos[i] = new OrderDto(rows[i].OrderId, rows[i].Total, rows[i].Status.ToString());
		return dtos;                                            // the pooled buffer is returned before we leave
	}
}

public sealed record OrderDto(string OrderId, decimal Total, string Status);
#endregion

public static class OrderServiceWiring {
	#region tutorial-register-caches
	public static void AddOrderCaches(this IServiceCollection services) {
		services.AddKafkaCaches(
			o => {
				o.BootstrapServers = "kafka:9092";
				o.Vars["env"] = "prod";                              // fills [v:env] in every topic template
			},
			b => {
				b.AddCache<OrderCache, string, Order>();            // topic "orders.[v:env]" from [DataCacheTopic] → "orders.prod"
				b.AddCache<CustomerCache, int, Customer>("customers.[v:env]");   // explicit template
				b.AddCache<OrderLineCache, string, OrderLine>("order-lines.[v:env]");
				b.AddCache<ShipmentCache, string, Shipment>("shipments.[v:env]")
					.WithAfterHandler<ShipmentNotifier>();          // step 7
			});

		services.AddSingleton<CustomerOrdersPage>();            // step 6: one frozen query for the process
	}
	#endregion

	#region tutorial-wait-for-load
	public static async Task WaitForCachesAsync(IHost host) {
		// Option A — a hosted service is registered for you: its StartAsync returns only after every cache has
		// replayed its topic to EOF, so host.RunAsync() / app.RunAsync() do not serve before the data is there.
		// Option B — somewhere else (a host that starts services concurrently, a test that built the provider):
		await host.DataCachesLoadCompletion();

		// Option C — anything that holds the registry can await the same completion:
		var registry = host.Services.GetRequiredService<IDataCacheRegistry>();
		await registry.LoadingCompletion;
	}
	#endregion

	#region tutorial-health
	public static void AddOrderHealth(this IServiceCollection services) {
		services.AddHealthChecks()
			.AddPragueKafkaLiveness(tags: ["live"])              // Unhealthy: consumer dead or stalled
			.AddPragueKafkaReadiness(tags: ["ready"]);           // Degraded: still loading, no partition, brokers down
	}

	public static void MapOrderHealth(this IEndpointRouteBuilder app) {
		app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
		app.MapHealthChecks("/health/ready", new HealthCheckOptions {
			Predicate = r => r.Tags.Contains("ready"),
			ResultStatusCodes = {
				[HealthStatus.Healthy] = StatusCodes.Status200OK,
				[HealthStatus.Degraded] = StatusCodes.Status503ServiceUnavailable,   // the default maps Degraded → 200
				[HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
			},
		});
	}
	#endregion

	#region tutorial-http-endpoint
	public static void MapOrderEndpoints(this IEndpointRouteBuilder app) {
		app.MapGet("/customers/{customerId:int}/orders",
			(int customerId, OrderStatus status, int skip, int take, CustomerOrdersPage page)
				=> Results.Ok(page.Get(customerId, status, skip, take)));   // DTOs: nothing pooled leaves the handler

		app.MapGet("/customers/{customerId:int}", (int customerId, CustomerCache customers)
			=> customers.Cache.TryGet(customerId, out var customer) ? Results.Ok(customer) : Results.NotFound());
	}
	#endregion

	#region tutorial-program
	public static WebApplication BuildOrderService(string[] args) {
		var builder = WebApplication.CreateBuilder(args);
		builder.Services.AddOrderCaches();                      // step 2
		builder.Services.AddOrderHealth();                      // step 8

		var app = builder.Build();
		app.MapOrderHealth();
		app.MapOrderEndpoints();                                // step 8
		return app;                                             // app.Run(): serving starts after the caches loaded
	}
	#endregion
}

[TestFixture]
public class BuildAnOrderService {
	private const long DayMs = 24 * 60 * 60 * 1000L;
	private static readonly long Now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	private DataCacheRegistry _registry = null!;
	private OrderCache _orders = null!;
	private CustomerCache _customers = null!;
	private OrderLineCache _lines = null!;

	[SetUp]
	public void SetUp() {
		#region tutorial-seed
		var registry = new DataCacheRegistryBuilder()           // in the service: AddKafkaCaches builds this for you
			.Register<OrderCache>()                              // pulls in Customer, OrderLine and Shipment through the FKs
			.Build();
		var orders = registry.GetCache<OrderCache>();
		var customers = registry.GetCache<CustomerCache>();
		var lines = registry.GetCache<OrderLineCache>();

		customers.AddOrUpdate(new Customer { Id = 42, Email = "ada@example.com", Country = "GB", Tier = CustomerTier.Gold, Name = "Ada" });
		customers.AddOrUpdate(new Customer { Id = 7, Email = "bob@example.com", Country = "US", Tier = CustomerTier.Standard, Name = "Bob" });

		orders.AddOrUpdate(NewOrder("ORD-001", 42, Now - 1 * DayMs, OrderStatus.Paid, 120m));
		orders.AddOrUpdate(NewOrder("ORD-002", 42, Now - 2 * DayMs, OrderStatus.Pending, 80m));
		orders.AddOrUpdate(NewOrder("ORD-003", 42, Now - 40 * DayMs, OrderStatus.Shipped, 15m));
		orders.AddOrUpdate(NewOrder("ORD-004", 7, Now - 3 * DayMs, OrderStatus.Paid, 60m));

		lines.AddOrUpdate(new OrderLine { LineId = "ORD-001/1", OrderId = "ORD-001", ProductId = 1, Quantity = 2, UnitPrice = 50m });
		lines.AddOrUpdate(new OrderLine { LineId = "ORD-001/2", OrderId = "ORD-001", ProductId = 2, Quantity = 1, UnitPrice = 20m });
		#endregion

		_registry = registry;
		_orders = orders;
		_customers = customers;
		_lines = lines;
	}

	[Test]
	public void RegistrationResolvesCachesWithoutABroker() {
		var services = new ServiceCollection();
		services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
		services.AddLogging();
		services.AddOrderCaches();

		using var provider = services.BuildServiceProvider();   // nothing is consumed until the host starts
		var orders = provider.GetRequiredService<OrderCache>();

		Assert.That(orders.Topic, Is.EqualTo("orders.prod"));
		Assert.That(provider.GetRequiredService<ShipmentCache>().Topic, Is.EqualTo("shipments.prod"));
		Assert.That(provider.GetRequiredService<CustomerOrdersPage>(), Is.Not.Null);
		Assert.That(provider.GetRequiredService<IDataCacheRegistry>().GetCaches().Select(c => c.Name),
			Is.EquivalentTo(new[] { "OrderCache", "CustomerCache", "OrderLineCache", "ShipmentCache", "ProductCache" }));
	}

	[Test]
	public void QueryLanes() {
		var orders = _orders;

		#region tutorial-query-lanes
		var thirtyDaysAgo = Now - 30 * DayMs;

		using var recent = orders.Query()
			.WithCustomerId(42)                                 // Many lane (from the foreign key)
			.WithCreatedAtUnixMs(q => q.Gte(thirtyDaysAgo))     // Range lane: intersected with the first
			.Where(o => o.Total > 100m)                         // residual filter on the survivors only
			.ExecutePooled();

		var openCount = orders.Query().WithStatus(OrderStatus.Pending).Count();   // no materialization
		#endregion

		Assert.That(recent.Count, Is.EqualTo(1));
		Assert.That(recent[0].OrderId, Is.EqualTo("ORD-001"));
		Assert.That(openCount, Is.EqualTo(1));
	}

	[Test]
	public void QueryOr() {
		var orders = _orders;

		#region tutorial-query-or
		// CustomerId = 42 AND (Status = Pending OR Status = Paid)
		using var actionable = orders.Query()
			.WithCustomerId(42)
			.Or(b => b.WithStatus(OrderStatus.Pending),
				b => b.WithStatus(OrderStatus.Paid))
			.ExecutePooled();
		#endregion

		Assert.That(actionable.Select(o => o.OrderId), Is.EquivalentTo(new[] { "ORD-001", "ORD-002" }));
	}

	[Test]
	public void QuerySortAndPage() {
		var orders = _orders;

		#region tutorial-query-sort-page
		using var page = orders.Query()
			.WithCustomerId(42)
			.SortBounded(OrderCache.ByCreatedAtComparer)        // from [DataCacheSort("ByCreatedAt", "CreatedAtUnixMs:-1", "OrderId:1")]
			.ExecutePooled(skip: 0, take: 2);                   // selects the page, never sorts the rest

		Console.WriteLine($"showing {page.Count} of {page.TotalCount}");
		#endregion

		Assert.That(page.Count, Is.EqualTo(2));
		Assert.That(page.TotalCount, Is.EqualTo(3));
		Assert.That(page.Select(o => o.OrderId), Is.EqualTo(new[] { "ORD-001", "ORD-002" }));
	}

	[Test]
	public void JoinOrdersWithCustomersAndLines() {
		var orders = _orders;

		#region tutorial-join
		using var rows = orders.Query()
			.WithCustomerId(42)
			.JoinWithCustomer()                                 // ManyToOne forward: row.Right is Customer?
			.JoinWithOrderLine()                                // 1:N reverse: row.Right2 is QueryResults<OrderLine>
			.ExecutePooled();                                   // one Dispose returns every buffer, lines included

		foreach (var row in rows)
			Console.WriteLine($"{row.Left.OrderId} for {row.Right?.Name ?? "unknown"}: {row.Right2.Count} line(s)");
		#endregion

		Assert.That(rows.Count, Is.EqualTo(3));
		var first = rows.Single(r => r.Left.OrderId == "ORD-001");
		Assert.That(first.Right!.Name, Is.EqualTo("Ada"));
		Assert.That(first.Right2.Count, Is.EqualTo(2));
		Assert.That(rows.Single(r => r.Left.OrderId == "ORD-002").Right2.Count, Is.Zero);
	}

	[Test]
	public void FrozenQueryServesPages() {
		var page = new CustomerOrdersPage(_orders);

		var paid = page.Get(42, OrderStatus.Paid, skip: 0, take: 10);
		var pending = page.Get(42, OrderStatus.Pending, skip: 0, take: 10);

		Assert.That(paid.Select(d => d.OrderId), Is.EqualTo(new[] { "ORD-001" }));
		Assert.That(pending.Select(d => d.OrderId), Is.EqualTo(new[] { "ORD-002" }));
		Assert.That(page.Get(7, OrderStatus.Paid, 0, 10).Single().Total, Is.EqualTo(60m));
	}

	[Test]
	public async Task AfterHandlerReactsToLiveUpdates() {
		#region tutorial-after-handler-test
		var notifier = new ShipmentNotifier();
		var shipment = new Shipment { ShipmentId = "SHP-1", OrderId = "ORD-001", Carrier = "DHL", ShippedAtUnixMs = Now };

		// The consumer calls Handle on the live phase; a unit test calls it directly.
		await notifier.Handle(UpdateType.Add, shipment.ShipmentId, shipment, null);
		await notifier.Handle(UpdateType.Same, shipment.ShipmentId, shipment, null);     // a re-emit: ignored
		#endregion

		Assert.That(notifier.Notified, Is.EqualTo(new[] { "ORD-001 shipped with DHL" }));
	}

	[Test]
	public async Task EndpointServesDtosFromTheCache() {
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Services.AddSingleton(_orders);                 // in the service these come from AddKafkaCaches
		builder.Services.AddSingleton(_customers);
		builder.Services.AddSingleton<CustomerOrdersPage>();
		await using var app = builder.Build();
		app.MapOrderEndpoints();
		await app.StartAsync();

		using var client = app.GetTestClient();
		var orders = await client.GetFromJsonAsync<OrderDto[]>("/customers/42/orders?status=Paid&skip=0&take=10");
		var customer = await client.GetFromJsonAsync<Customer>("/customers/42");
		var missing = await client.GetAsync("/customers/999");

		Assert.That(orders!.Select(o => o.OrderId), Is.EqualTo(new[] { "ORD-001" }));
		Assert.That(customer!.Name, Is.EqualTo("Ada"));
		Assert.That(missing.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotFound));
		await app.StopAsync();
	}

	private static Order NewOrder(string orderId, int customerId, long createdAtUnixMs, OrderStatus status, decimal total) => new() {
		OrderId = orderId,
		CustomerId = customerId,
		CreatedAtUnixMs = createdAtUnixMs,
		Status = status,
		Total = total,
	};
}
