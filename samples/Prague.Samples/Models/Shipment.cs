// docs: the 1:1 side of Order — OrderCache.JoinWithShipment() yields Shipment? and
// ShipmentCache.JoinWithOrder() yields Order?. Used for outer-vs-inner join examples.
namespace Prague.Samples.Models;

using Prague.Core;

[DataCache]
public partial class Shipment {
	[DataCacheKey]
	public required string ShipmentId { get; init; }

	[DataCacheForeignKey<Order>(DataCacheJoinType.OneToOne)]       // one shipment per order
	public required string OrderId { get; init; }

	[DataCacheIndex(DataCacheIndexType.Many)]
	public required string Carrier { get; init; }

	public required long ShippedAtUnixMs { get; init; }
}
