// docs: every code block of www/docs/articles/how-to/write-a-filter.md. Filters are registered on the Kafka
// handler builder, so each block is a compiled-only registration; the one test proves the registrations build
// a container (nothing is consumed — handlers are built when the hosted service starts).
namespace Prague.Samples.HowTo;

using System.Collections.Frozen;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Prague.Core;
using Prague.Kafka;
using Prague.Samples.Models;

#region filter-allow-list-service
public interface ICustomerAllowList {
	IReadOnlySet<int> CustomerIds { get; }
}

public sealed class StaticCustomerAllowList : ICustomerAllowList {
	public IReadOnlySet<int> CustomerIds { get; } = new HashSet<int> { 42, 7 };
}
#endregion

public static class OrderFilters {
	#region filter-header
	public static void HeaderFilters(KafkaCacheHandlersBuilder b) {
		b.AddCache<OrderCache, string, Order>()
			.WithHeaderEqualsFilter("tenant", "acme")                       // string: UTF-8 compare, no allocation
			.WithHeaderEqualsFilter("region", "EU", "UK")                   // several values: OR within the header
			.WithHeaderNotEqualsFilter("source", "synthetic")
			.WithHeaderExistsFilter("tenant")                               // the header must be present (tombstones exempt)
			.WithHeaderFilter<long>("schema-version", v => v >= 3, passOnNull: false);   // MessagePack-decoded value
	}
	#endregion

	#region filter-key-value
	public static void KeyAndValueFilters(KafkaCacheHandlersBuilder b) {
		b.AddCache<OrderCache, string, Order>()
			.WithKeyFilter(static key => !key.StartsWith("TEST-", StringComparison.Ordinal))   // after key deserialization
			.WithValueFilter(static o => o.Total > 0m);                                         // after value deserialization
	}
	#endregion

	#region filter-treat-as-delete
	public static void SoftDeletesBecomeTombstones(KafkaCacheHandlersBuilder b) {
		b.AddCache<OrderCache, string, Order>()
			// A cancelled order is removed from the cache (UpdateType.Delete on the live phase) instead of merely skipped.
			.WithValueFilter(static o => o.Status != OrderStatus.Cancelled, treatAsDelete: true);
	}
	#endregion

	#region filter-di-state
	public static void FiltersWithDiState(KafkaCacheHandlersBuilder b) {
		b.AddCache<OrderCache, string, Order>()
			// Snapshot: the factory runs once while the handler is built; the predicate gets the frozen set as an argument.
			.WithValueFilter(
				static sp => sp.GetRequiredService<ICustomerAllowList>().CustomerIds.ToFrozenSet(),
				static (allowed, o) => allowed.Contains(o.CustomerId))
			// Live service: TService is spelled out; the predicate reads the service's current state per message.
			.WithValueFilter<ICustomerAllowList>(static (allow, o) => allow.CustomerIds.Contains(o.CustomerId));
	}
	#endregion

	#region filter-first-reject-wins
	public static void OrderMatters(KafkaCacheHandlersBuilder b) {
		b.AddCache<OrderCache, string, Order>()
			.WithValueFilter(static o => o.Total > 0m)                                          // rejects → Skip
			.WithValueFilter(static o => o.Status != OrderStatus.Cancelled, treatAsDelete: true); // rejects → Delete
		// A cancelled order with Total 0 is Skipped, not Deleted: the first rejecting filter decides.
	}
	#endregion
}

[TestFixture]
public class WriteAFilter {
	[TestCase(nameof(OrderFilters.HeaderFilters))]
	[TestCase(nameof(OrderFilters.KeyAndValueFilters))]
	[TestCase(nameof(OrderFilters.SoftDeletesBecomeTombstones))]
	[TestCase(nameof(OrderFilters.FiltersWithDiState))]
	[TestCase(nameof(OrderFilters.OrderMatters))]
	public void RegistrationsBuildAContainer(string method) {
		var configure = (Action<KafkaCacheHandlersBuilder>)Delegate.CreateDelegate(
			typeof(Action<KafkaCacheHandlersBuilder>), typeof(OrderFilters).GetMethod(method)!);

		var services = new ServiceCollection();
		services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
		services.AddLogging();
		services.AddSingleton<ICustomerAllowList, StaticCustomerAllowList>();
		services.AddKafkaCaches(o => o.BootstrapServers = "kafka:9092", configure);

		using var provider = services.BuildServiceProvider();

		// The cache resolves and is empty; the filter chain is built when the consumer starts, not here.
		Assert.That(provider.GetRequiredService<OrderCache>().Query().WithCustomerId(42).Count(), Is.Zero);
	}
}
