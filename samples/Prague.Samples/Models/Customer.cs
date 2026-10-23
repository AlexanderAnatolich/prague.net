// docs: the parent side of the Order → Customer join. Carries a Unique index (Email), two Many
// indices used as join-filter and Or examples (Country, Tier), and is the primary-key group of
// CustomerActivityIndex, which gives OrderCache/CustomerCache the generated UpdatedAfter(...) verb.
namespace Prague.Samples.Models;

using Prague.Core;

public enum CustomerTier {
	Standard,
	Gold,
}

[DataCache]
public partial class Customer {
	[DataCacheKey]
	[DataCacheGlobalLastUpdateIndex<CustomerActivityIndex>]        // group key == primary key → customerCache.Query().UpdatedAfter(...)
	public required int Id { get; init; }

	[DataCacheIndex(DataCacheIndexType.Unique)]                    // 1:1 — alternate key
	public required string Email { get; init; }

	[DataCacheIndex(DataCacheIndexType.Many)]
	public required string Country { get; init; }

	[DataCacheIndex(DataCacheIndexType.Many)]
	public required CustomerTier Tier { get; init; }

	public required string Name { get; init; }
}
