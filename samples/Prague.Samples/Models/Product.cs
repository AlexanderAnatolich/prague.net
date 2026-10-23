// docs: the index-type showcase — Unique (Sku), explicit symmetric Many (CategoryId), Many with a
// bucket-capacity hint (Brand), Range over a decimal (Price), a named value index (InStock), has-value
// indices on a nullable and on a bool, [DataCacheIgnoreEquality] for conditional updates, and a
// [DataCacheSort] comparer for paging. OrderLine points at it with a ManyToOne foreign key.
namespace Prague.Samples.Models;

using Prague.Core;

[DataCache]
[DataCacheSort("ByPrice", "Price:1", "Id:1")]                   // total order: Id breaks price ties
public partial class Product {
	[DataCacheKey]
	public required int Id { get; init; }

	[DataCacheIndex(DataCacheIndexType.Unique)]
	public required string Sku { get; init; }

	[DataCacheIndex(DataCacheIndexType.Many, Symmetric = true)]    // reverse map (Id → CategoryId) for index-driven joins
	public required int CategoryId { get; init; }

	[DataCacheIndex(DataCacheIndexType.Many, 4)]                   // bucket capacity hint: few products per brand
	public required string Brand { get; init; }

	[DataCacheIndex(DataCacheIndexType.Range)]
	public required decimal Price { get; init; }

	[DataCacheValueIndex(CompareOp.GreaterThan, 0, IndexName = "InStock")]   // key set: WithInStock() = Stock > 0
	public required int Stock { get; init; }

	[DataCacheHasValueIndex]                                       // bool: WithIsPublished() = IsPublished == true
	public required bool IsPublished { get; init; }

	[DataCacheHasValueIndex]                                       // nullable: WithDiscontinuedAtUnixMs() = non-null
	public long? DiscontinuedAtUnixMs { get; init; }

	public required string Name { get; init; }

	[DataCacheIgnoreEquality]                                      // changes here never count as an Update
	public long LastSeenAtUnixMs { get; init; }
}
