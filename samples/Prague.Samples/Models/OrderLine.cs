// docs: the 1:N side of Order (OrderCache.JoinWithOrderLine() yields QueryResults<OrderLine>) and a
// second forward ManyToOne join to Product — the chained-join and inner-join examples.
namespace Prague.Samples.Models;

using Prague.Core;

[DataCache]
public partial class OrderLine {
	[DataCacheKey]
	public required string LineId { get; init; }                  // "ORD-001/1"

	[DataCacheForeignKey<Order>(DataCacheJoinType.ManyToOne)]      // many lines → one order
	public required string OrderId { get; init; }

	[DataCacheForeignKey<Product>(DataCacheJoinType.ManyToOne)]    // many lines → one product
	public required int ProductId { get; init; }

	public required int Quantity { get; init; }

	public required decimal UnitPrice { get; init; }
}
