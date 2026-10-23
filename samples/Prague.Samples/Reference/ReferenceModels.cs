// docs: models that exist only for the attribute reference page (www/docs/articles/reference/attributes.md).
// The shared Models/ cover the common attributes; these cover the rest — a renamed cache class, header
// and timestamp enrichment, has-not-value / no-value key sets, the filtered and timestamp-property forms
// of the global last-update index, a selector-form foreign key, the dual-form foreign key, an external
// type cached through [DataCache<T>], [DataCacheFrom<T>] and [DataCacheJsonSerializables].
namespace Prague.Samples.Reference;

using System.Text.Json.Serialization;
using Prague.Core;
using Prague.Samples.Models;

#region audit-event
[DataCache("AuditLog")]                                        // the cache class is AuditLog, not AuditEventCache
public partial class AuditEvent {
	[DataCacheKey]
	public required long EventId { get; init; }

	[DataCacheForeignKey<Order, OrderRefSelector>(DataCacheJoinType.ManyToOne)]   // "ORD-001#3" → Order "ORD-001" at join time
	public required string OrderRef { get; init; }

	[DataCacheGlobalLastUpdateIndex<AuditActivityIndex>(nameof(OccurredAtUnixMs))]              // timestamp read from the property
	[DataCacheGlobalLastUpdateIndex<AuditErrorIndex, ErrorsOnly>(nameof(OccurredAtUnixMs))]     // same, but only rows ErrorsOnly admits
	public required int CustomerId { get; init; }

	public required long OccurredAtUnixMs { get; init; }

	[DataCacheHasValueIndex]                                    // WithResolvedAtUnixMs()    → non-null
	[DataCacheHasNotValueIndex]                                 // WithoutResolvedAtUnixMs() → null
	public long? ResolvedAtUnixMs { get; init; }

	[DataCacheValueIndex(CompareOp.GreaterThanOrEqual, 3)]     // AttemptsGreaterThanOrEqual3Index / WithAttemptsGreaterThanOrEqual3()
	public required int Attempts { get; init; }

	[DataCacheNoValueIndex(CompareOp.Equal, "info", IndexName = "NotInfoIndex")]   // NotInfoIndex / WithNotInfo(): Severity != "info"
	public required string Severity { get; init; }

	[DataCacheHeader("tenant-id")]                              // read from / written to the Kafka header "tenant-id"
	public int TenantId { get; set; }

	[DataCacheFromTimestamp]                                    // set from the Kafka message timestamp on consume
	public long ReceivedAtUnixMs { get; set; }
}

public readonly struct OrderRefSelector : IForeignKeySelector<string, string> {
	public static string Select(string orderRef) {
		var hash = orderRef.IndexOf('#');
		return hash < 0 ? orderRef : orderRef[..hash];
	}
}

public readonly struct ErrorsOnly : IDataCacheGlobalLastUpdateFilter<AuditEvent> {
	public static bool Include(AuditEvent value) => value.Severity == "error";
}

public partial class AuditActivityIndex : IDataCacheGlobalLastUpdateIndex<int>;

public partial class AuditErrorIndex : IDataCacheGlobalLastUpdateIndex<int>;
#endregion

#region dual-form-fk
[DataCache]
public partial class Warehouse {
	[DataCacheKey]
	[DataCacheForeignKey<StockLevel>(DataCacheJoinType.OneToMany, nameof(StockLevel.WarehouseId))]   // == ManyToOne on StockLevel.WarehouseId
	public required int Id { get; init; }

	public required string Code { get; init; }
}

[DataCache]
public partial class StockLevel {
	[DataCacheKey]
	public required string Id { get; init; }

	public required int WarehouseId { get; init; }             // no attribute here — the FK is declared on Warehouse.Id

	public required int Quantity { get; init; }
}
#endregion

#region external-cache
public partial class LegacyProductRecord {                    // a type you cannot annotate; it must still be partial (see the page)
	public int Code { get; set; }
	public string Category { get; set; } = "";
	public decimal Price { get; set; }
	public long SyncedAtUnixMs { get; set; }
}

[DataCache<LegacyProductRecord>(nameof(LegacyProductRecord.Code))]
[DataCacheTopic("legacy.products")]
[DataCacheIndex(nameof(LegacyProductRecord.Category), DataCacheIndexType.Many)]
[DataCacheIndex(nameof(LegacyProductRecord.Price), "PriceBand", DataCacheIndexType.Range)]   // field PriceBandIndex, WithPriceBand(...)
[DataCacheIgnoreEquality(nameof(LegacyProductRecord.SyncedAtUnixMs))]
public partial class LegacyProductCache;
#endregion

#region datacache-from
public sealed class SupplierDto {                             // the source type — a wire contract you do not own
	public int Id { get; set; }
	public string Name { get; set; } = "";
}

[DataCacheFrom<SupplierDto>("4025d7aa-394e-5318-b5c6-bcd27d13721b")]   // hash written by the code fix; stale → DATACACHE002
[DataCache]
public partial class Supplier {
	[DataCacheKey]
	public int Id { get; set; }                                 // the code fix generated these two; you own them from here

	public string Name { get; set; } = "";
}
#endregion

#region json-serializables
[DataCacheJsonSerializables]
[JsonSerializable(typeof(Order))]
public partial class SamplesJsonContext : JsonSerializerContext;
#endregion
