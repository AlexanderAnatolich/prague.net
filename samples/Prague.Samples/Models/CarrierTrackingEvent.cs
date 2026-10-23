// docs: the external-type form in index-types.md. CarrierTrackingEvent stands in for a type whose source
// you do not control but which is compiled with you — a protobuf or OpenAPI-generated class. Such types
// are partial (the generator completes them with CacheEquals / Clone) but carry no Prague attributes;
// the cache class declares the key and the indices by property name instead.
namespace Prague.Samples.Models;

using Prague.Core;

public partial class CarrierTrackingEvent {                     // generated elsewhere: partial, no attributes
	public string EventId { get; set; } = "";
	public string ShipmentId { get; set; } = "";
	public string Status { get; set; } = "";
	public long? DeliveredAtUnixMs { get; set; }
}

[DataCache<CarrierTrackingEvent>(nameof(CarrierTrackingEvent.EventId))]                 // key property by name
[DataCacheIndex(nameof(CarrierTrackingEvent.ShipmentId), DataCacheIndexType.Many)]     // WithShipmentId(...)
[DataCacheIndex(nameof(CarrierTrackingEvent.Status), DataCacheIndexType.Many, 4)]      // WithStatus(...), bucket hint
public partial class CarrierTrackingEventCache;
