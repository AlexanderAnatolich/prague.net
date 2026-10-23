// docs: the selector-form foreign key in joins.md. A shipment travels in numbered hops; the leg stores
// its place in the route as the compound (ShipmentId, Sequence), not as a bare shipment id, so the
// generated join cannot compare the property to Shipment's primary key directly. The selector hands
// it the ShipmentId half. ManyToOne: several legs resolve to the same shipment. Forward join only:
// ShipmentLegCache.JoinWithShipment() yields Shipment?; the selector side gets no reverse join.
// Also the home of the has-not-value verb: WithoutArrivedAtUnixMs() in index-types.md.
namespace Prague.Samples.Models;

using Prague.Core;

public readonly struct ShipmentLegShipmentKey : IForeignKeySelector<(string ShipmentId, int Sequence), string> {
	public static string Select((string ShipmentId, int Sequence) hop) => hop.ShipmentId;
}

[DataCache]
public partial class ShipmentLeg {
	[DataCacheKey]
	public required string LegId { get; init; }                   // "SHP-1/1"

	[DataCacheForeignKey<Shipment, ShipmentLegShipmentKey>(DataCacheJoinType.ManyToOne)]   // (ShipmentId, Sequence) → Shipment.ShipmentId
	public required (string ShipmentId, int Sequence) Hop { get; init; }

	public required string FromHub { get; init; }

	public required string ToHub { get; init; }

	[DataCacheHasNotValueIndex]                                    // key set: WithoutArrivedAtUnixMs() = still in flight
	public long? ArrivedAtUnixMs { get; init; }
}
