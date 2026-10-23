// docs: every code block of www/docs/articles/how-to/inspect-over-http.md — mounting Prague.Api's endpoints,
// what they return, and putting them behind authorization. The test runs the real endpoints in-process
// (TestServer) over a registry built by hand: MapPragueEndpoints needs IDataCacheRegistry,
// KafkaCachesStatistics and KafkaCachesGlobalOptions, all of which AddKafkaCaches registers in a real host.
namespace Prague.Samples.HowTo;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Prague.Api;
using Prague.Core;
using Prague.Kafka;
using Prague.Samples.Models;

public static class InspectOverHttpWiring {
	#region inspect-mount
	public static void MountOpen(WebApplication app) {
		app.MapPragueEndpoints();      // GET /prague · GET /prague/{CacheName} · GET /prague/{CacheName}/{id} (parsable keys only)
	}
	#endregion

	#region inspect-secure
	public static void MountSecured(WebApplication app) {
		// MapPragueEndpoints maps onto any IEndpointRouteBuilder, so a route group gives it a prefix and a policy.
		var ops = app.MapGroup("/internal").RequireAuthorization("ops");   // → /internal/prague/...
		ops.MapPragueEndpoints();
	}

	public static void MountOnLoopbackOnly(IEndpointRouteBuilder app) {
		app.MapGroup("").RequireHost("localhost", "127.0.0.1").MapPragueEndpoints();   // or bind a separate management port
	}
	#endregion
}

[TestFixture]
public class InspectOverHttp {
	private WebApplication _app = null!;
	private HttpClient _client = null!;

	[OneTimeSetUp]
	public async Task StartAsync() {
		var registry = new DataCacheRegistryBuilder().Register<OrderCache>().Build();
		var orders = registry.GetCache<OrderCache>();
		orders.AddOrUpdate(new Order { OrderId = "ORD-001", CustomerId = 42, CreatedAtUnixMs = 1_000, Status = OrderStatus.Paid, Total = 120m });
		orders.AddOrUpdate(new Order { OrderId = "ORD-002", CustomerId = 42, CreatedAtUnixMs = 2_000, Status = OrderStatus.Pending, Total = 80m });
		orders.AddOrUpdate(new Order { OrderId = "ORD-003", CustomerId = 7, CreatedAtUnixMs = 3_000, Status = OrderStatus.Paid, Total = 60m });
		registry.GetCache<CustomerCache>().AddOrUpdate(new Customer { Id = 42, Email = "ada@example.com", Country = "GB", Tier = CustomerTier.Gold, Name = "Ada" });

		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseTestServer();
		builder.Services.AddSingleton<IDataCacheRegistry>(registry);   // AddKafkaCaches registers these three for you
		builder.Services.AddSingleton<KafkaCachesStatistics>();
		builder.Services.AddOptions();
		_app = builder.Build();
		InspectOverHttpWiring.MountOpen(_app);
		await _app.StartAsync();
		_client = _app.GetTestClient();
	}

	[OneTimeTearDown]
	public async Task StopAsync() {
		_client.Dispose();
		await _app.StopAsync();
		await _app.DisposeAsync();
	}

	[Test]
	public async Task StatisticsEndpoint() {
		#region inspect-statistics
		var statistics = await _client.GetStringAsync("/prague");   // consumers → caches → sizes, load times, index sizes
		#endregion

		using var doc = JsonDocument.Parse(statistics);
		Assert.That(doc.RootElement.TryGetProperty("Consumers", out _), Is.True);   // PascalCase, source-generated JSON
	}

	[Test]
	public async Task CollectionEndpointRunsTheQueryStringParser() {
		#region inspect-query-string
		var byCustomer = await _client.GetStringAsync("/prague/OrderCache?customerId=42");          // index lane
		var paidAndBig = await _client.GetStringAsync("/prague/OrderCache?status=Paid&total.gte=100");   // lane + residual filter
		var window = await _client.GetStringAsync("/prague/OrderCache?createdAtUnixMs=1000..2000&size=1&from=1");   // range + page
		var firstHundred = await _client.GetStringAsync("/prague/OrderCache");                      // no query: first 100 rows
		// Typed into a browser, `total>=100` works too; HttpClient percent-encodes `>` and `[`, which the parser does not decode.
		#endregion

		Assert.That(Ids(byCustomer), Is.EquivalentTo(new[] { "ORD-001", "ORD-002" }));
		Assert.That(Ids(paidAndBig), Is.EqualTo(new[] { "ORD-001" }));
		Assert.That(Ids(window), Has.Length.EqualTo(1).And.SubsetOf(new[] { "ORD-001", "ORD-002" }));
		Assert.That(Ids(firstHundred), Has.Length.EqualTo(3));
	}

	[Test]
	public async Task ByIdEndpointExistsOnlyForParsableKeys() {
		// OrderCache.IsKeyParsable is false (string key): the generator emits no /{id} route for it.
		var response = await _client.GetAsync("/prague/OrderCache/ORD-001");
		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
		Assert.That(OrderCache.IsKeyParsable, Is.False);
	}

	[Test]
	public async Task UnknownCacheIs404() {
		var response = await _client.GetAsync("/prague/NoSuchCache");
		Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
	}

	private static string[] Ids(string json) {
		using var doc = JsonDocument.Parse(json);
		return doc.RootElement.EnumerateArray().Select(e => e.GetProperty("orderId").GetString()!).ToArray();
	}
}
