// docs: every code block of www/docs/articles/core-concepts/sorted-paging.md — Sort versus SortBounded,
// a total comparer, and what the two plans do with comparer ties.
namespace Prague.Samples.Concepts;

using NUnit.Framework;
using Prague.Core;
using Prague.Samples.Models;

#region total-comparer
public readonly struct ByTotalDescThenOrderId : IComparer<Order> {
	public int Compare(Order? x, Order? y) {
		var byTotal = y!.Total.CompareTo(x!.Total);
		return byTotal != 0 ? byTotal : string.CompareOrdinal(x.OrderId, y.OrderId);   // total: no ties between distinct rows
	}
}
#endregion

#region tied-comparer
public readonly struct ByStatus : IComparer<Order> {                                  // ties: many orders share a status
	public int Compare(Order? x, Order? y) => x!.Status.CompareTo(y!.Status);
}
#endregion

[TestFixture]
public class SortedPaging {
	private const int OrderCount = 120;

	private OrderCache _orders = null!;

	[SetUp]
	public void SetUp() {
		_orders = new OrderCache();
		for (var i = 1; i <= OrderCount; i++)
			_orders.AddOrUpdate(new Order {
				OrderId = $"ORD-{i:D3}",
				CustomerId = 42,
				CreatedAtUnixMs = 1_760_000_000_000L + i * 1000L,
				Status = (OrderStatus)(i % 4),
				Total = i % 10,                                                    // ten orders share each total
			});
	}

	[Test]
	public void SortVersusSortBounded() {
		#region sort-vs-sort-bounded
		// Paging a large result: select just the page, never sort the rest.
		using var page = _orders.Query()
			.WithCustomerId(42)
			.SortBounded(OrderCache.ByCreatedAtComparer)
			.ExecutePooled(skip: 40, take: 20);

		// Materialising a whole result: classic full sort.
		using var all = _orders.Query()
			.WithCustomerId(42)
			.Sort(OrderCache.ByCreatedAtComparer)
			.ExecutePooled();
		#endregion

		Assert.That(page.Count, Is.EqualTo(20));
		Assert.That(page.TotalCount, Is.EqualTo(OrderCount));
		Assert.That(all.Count, Is.EqualTo(OrderCount));
		Assert.That(Ids(page), Is.EqualTo(Ids(all).Skip(40).Take(20)));
	}

	[Test]
	public void TotalComparerMakesBothPlansIdentical() {
		#region total-comparer-pages
		using var classic = _orders.Query().WithCustomerId(42).Sort(new ByTotalDescThenOrderId()).ExecutePooled(skip: 30, take: 25);
		using var bounded = _orders.Query().WithCustomerId(42).SortBounded(new ByTotalDescThenOrderId()).ExecutePooled(skip: 30, take: 25);
		#endregion

		Assert.That(Ids(bounded), Is.EqualTo(Ids(classic)));
	}

	[Test]
	public void TiesKeepEncounterOrderOnBothPlans() {
		#region tied-comparer-plans-agree
		using var classic = _orders.Query().WithCustomerId(42).Sort(new ByStatus()).ExecutePooled();
		using var bounded = _orders.Query().WithCustomerId(42).SortBounded(new ByStatus()).ExecutePooled(skip: 0, take: OrderCount);
		#endregion

		Assert.That(Ids(bounded), Is.EqualTo(Ids(classic)), "both plans are stable: comparer-equal rows keep encounter order");
	}

	[Test]
	public void PagesPartitionAnUnchangedResult() {
		#region pages-partition
		var seen = new List<string>();
		for (var skip = 0; skip < OrderCount; skip += 25) {
			using var page = _orders.Query().WithCustomerId(42).SortBounded(new ByStatus()).ExecutePooled(skip, take: 25);
			seen.AddRange(Ids(page));
		}
		#endregion

		Assert.That(seen, Has.Count.EqualTo(OrderCount));
		Assert.That(seen.Distinct().Count(), Is.EqualTo(OrderCount), "pages overlapped");
	}

	private static string[] Ids(QueryResults<Order> orders) {
		var ids = new string[orders.Count];
		for (var i = 0; i < ids.Length; i++)
			ids[i] = orders[i].OrderId;
		return ids;
	}
}
