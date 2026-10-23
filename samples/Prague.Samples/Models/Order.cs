// docs: the entity every page starts from — Quick Start, index types (Range, Many, has-value),
// query engine, sorted paging, conditional updates, Kafka topic + filters. It owns a ManyToOne
// foreign key to Customer (the forward join side) and is the left side of the 1:1 join to
// Shipment and the 1:N join to OrderLine.
namespace Prague.Samples.Models;

using Prague.Core;

#region order-entity
public enum OrderStatus {
	Pending,
	Paid,
	Shipped,
	Cancelled,
}

[DataCache]                                                     // emits OrderCache; [DataCache("OrdersCache")] renames it
[DataCacheTopic("orders.[v:env]")]                              // Kafka topic; [v:env] resolves from KafkaCachesOptions.Vars
[DataCacheSort("ByCreatedAt", "CreatedAtUnixMs:-1", "OrderId:1")] // emits OrderCache.ByCreatedAtComparer (newest first)
public partial class Order {                                    // must be partial
	[DataCacheKey]
	public required string OrderId { get; init; }

	[DataCacheForeignKey<Customer>(DataCacheJoinType.ManyToOne)]   // many orders → one customer; also indexes CustomerId
	[DataCacheGlobalLastUpdateIndex<CustomerActivityIndex>]        // every order write stamps its customer's activity
	public required int CustomerId { get; init; }

	[DataCacheIndex(DataCacheIndexType.Range)]                     // sorted — Gte / Lt / … queries
	public required long CreatedAtUnixMs { get; init; }

	[DataCacheIndex(DataCacheIndexType.Many)]                      // 1:N — many orders share a status
	public required OrderStatus Status { get; init; }

	[DataCacheHasValueIndex]                                       // key set: WithSettledAtUnixMs() / WithoutSettledAtUnixMs()
	public long? SettledAtUnixMs { get; init; }

	public required decimal Total { get; init; }
}
#endregion
