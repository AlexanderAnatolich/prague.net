// docs: every code block of www/docs/articles/how-to/page-an-api.md — a paged, sorted list endpoint over
// OrderCache: SortBounded vs Sort, a total struct comparer, offset and cursor pages, DTOs that leave the
// pooled buffer behind, and the same page as a frozen query with a parameter struct.
namespace Prague.Samples.HowTo;

using NUnit.Framework;
using Prague.Core;
using Prague.Samples.Models;

#region page-total-comparer
/// <summary>Newest first; OrderId breaks ties, so two distinct orders never compare equal.</summary>
public readonly struct ByCreatedAtDescThenId : IComparer<Order> {
	public int Compare(Order? x, Order? y) {
		var byDate = y!.CreatedAtUnixMs.CompareTo(x!.CreatedAtUnixMs);
		return byDate != 0 ? byDate : string.CompareOrdinal(x.OrderId, y.OrderId);
	}
}
#endregion

#region page-dto
public sealed record OrderSummary(string OrderId, long CreatedAtUnixMs, decimal Total);

public sealed record OrderPage(OrderSummary[] Items, int TotalCount, string? NextCursor);
#endregion

#region page-prepared
public sealed class CustomerOrderPages {
	public readonly record struct Args(int CustomerId);                 // TArgs must be a struct

	private readonly FrozenQuery<Args, Order> _byCustomer;

	public CustomerOrderPages(OrderCache orders) {
		_byCustomer = orders.Prepare<Args>()
			.WithCustomerId(static a => a.CustomerId)
			.SortBounded(new ByCreatedAtDescThenId())                      // total comparer: pages identical to eager
			.BuildFrozen();
	}

	public OrderPage Get(int customerId, int skip, int take) {
		using var rows = _byCustomer.ExecutePooled(new Args(customerId), skip, take);   // 0 B per call
		return new OrderPage(ToSummaries(rows), rows.TotalCount, NextCursor: null);
	}

	internal static OrderSummary[] ToSummaries(QueryResults<Order> rows) {
		var items = new OrderSummary[rows.Count];
		for (var i = 0; i < rows.Count; i++)
			items[i] = new OrderSummary(rows[i].OrderId, rows[i].CreatedAtUnixMs, rows[i].Total);
		return items;
	}
}
#endregion

[TestFixture]
public class PageAnApi {
	private const long HourMs = 60 * 60 * 1000L;
	private const long Base = 1_700_000_000_000L;

	private OrderCache _orders = null!;

	[SetUp]
	public void SetUp() {
		_orders = new OrderCache();
		for (var i = 0; i < 25; i++)
			_orders.AddOrUpdate(new Order {
				OrderId = $"ORD-{i:000}",
				CustomerId = 42,
				CreatedAtUnixMs = Base + (i / 5) * HourMs,            // five orders share each timestamp: ties
				Status = OrderStatus.Paid,
				Total = 10m * i,
			});
		_orders.AddOrUpdate(new Order { OrderId = "OTHER", CustomerId = 7, CreatedAtUnixMs = Base, Status = OrderStatus.Paid, Total = 1m });
	}

	[Test]
	public void OffsetPageWithSortBounded() {
		var orders = _orders;
		var skip = 10;
		var take = 5;

		#region page-offset
		using var page = orders.Query()
			.WithCustomerId(42)
			.SortBounded(new ByCreatedAtDescThenId())           // top-k: only the page is sorted
			.ExecutePooled(skip, take);

		var response = new OrderPage(
			CustomerOrderPages.ToSummaries(page),               // copy into DTOs while the buffer is alive
			page.TotalCount,                                    // matches before paging: "showing 5 of 25"
			NextCursor: null);
		#endregion

		Assert.That(response.Items, Has.Length.EqualTo(5));
		Assert.That(response.TotalCount, Is.EqualTo(25));
		Assert.That(response.Items.Select(i => i.OrderId), Is.EqualTo(new[] { "ORD-010", "ORD-011", "ORD-012", "ORD-013", "ORD-014" }));   // 3rd-newest timestamp, ids ascending
	}

	[Test]
	public void WholeResultWithSort() {
		var orders = _orders;

		#region page-whole-result
		using var all = orders.Query()
			.WithCustomerId(42)
			.Sort(OrderCache.ByCreatedAtComparer)               // the generated comparer: "CreatedAtUnixMs:-1", "OrderId:1"
			.ExecutePooled();                                   // no page: Sort is the right plan
		#endregion

		Assert.That(all.Count, Is.EqualTo(25));
		Assert.That(all[0].OrderId, Is.EqualTo("ORD-020"));
		Assert.That(all[^1].OrderId, Is.EqualTo("ORD-004"));
	}

	[Test]
	public void PagesPartitionTheResult() {
		var seen = new List<string>();
		for (var skip = 0; skip < 25; skip += 7) {
			using var page = _orders.Query().WithCustomerId(42).SortBounded(new ByCreatedAtDescThenId()).ExecutePooled(skip, 7);
			seen.AddRange(page.Select(o => o.OrderId));
		}

		// A total comparer: consecutive pages have no duplicates and no gaps.
		Assert.That(seen, Is.Unique);
		Assert.That(seen, Has.Count.EqualTo(25));
	}

	[Test]
	public void CursorPage() {
		var orders = _orders;

		var first = CursorPage(orders, after: null, take: 10);
		var second = CursorPage(orders, first.NextCursor, take: 10);
		var third = CursorPage(orders, second.NextCursor, take: 10);

		var ids = first.Items.Concat(second.Items).Concat(third.Items).Select(i => i.OrderId).ToList();
		Assert.That(ids, Is.Unique);
		Assert.That(ids, Has.Count.EqualTo(25));
		Assert.That(third.Items, Has.Length.EqualTo(5));
		Assert.That(third.NextCursor, Is.Null);
	}

	#region page-cursor
	// Cursor = the last row's (CreatedAtUnixMs, OrderId). Everything strictly after it in sort order is the next page.
	private static OrderPage CursorPage(OrderCache orders, string? after, int take) {
		var query = orders.Query().WithCustomerId(42);

		if (after is null) {
			using var firstPage = query.SortBounded(new ByCreatedAtDescThenId()).ExecutePooled(0, take);
			return ToPage(firstPage, take);
		}

		var cut = after.IndexOf('|');
		var cursorTs = long.Parse(after[..cut]);
		var cursorId = after[(cut + 1)..];
		using var page = query
			.WithCreatedAtUnixMs(q => q.Lte(cursorTs))          // Range lane: everything at or before the cursor time
			.Where(o => o.CreatedAtUnixMs < cursorTs || string.CompareOrdinal(o.OrderId, cursorId) > 0)   // skip the tie rows already served
			.SortBounded(new ByCreatedAtDescThenId())
			.ExecutePooled(0, take);
		return ToPage(page, take);
	}

	private static OrderPage ToPage(QueryResults<Order> rows, int take) {
		var items = CustomerOrderPages.ToSummaries(rows);
		var last = rows.Count == take ? rows[^1] : null;
		return new OrderPage(items, rows.TotalCount, last is null ? null : $"{last.CreatedAtUnixMs}|{last.OrderId}");
	}
	#endregion

	[Test]
	public void PreparedPageMatchesEager() {
		var pages = new CustomerOrderPages(_orders);

		var frozen = pages.Get(42, skip: 10, take: 5);
		using var eager = _orders.Query().WithCustomerId(42).SortBounded(new ByCreatedAtDescThenId()).ExecutePooled(10, 5);

		Assert.That(frozen.Items.Select(i => i.OrderId), Is.EqualTo(eager.Select(o => o.OrderId)));
		Assert.That(frozen.TotalCount, Is.EqualTo(25));
		Assert.That(pages.Get(7, 0, 10).Items.Single().OrderId, Is.EqualTo("OTHER"));
	}

	[Test]
	public void TiesWithoutATieBreakAreUnspecifiedUnderSort() {
		// CreatedAtUnixMs alone ties five rows; Sort keeps encounter order, which is not a contract. Only the set is.
		using var a = _orders.Query().WithCustomerId(42).Sort(new ByCreatedAtOnly()).ExecutePooled(0, 5);
		Assert.That(a.Select(o => o.CreatedAtUnixMs), Is.All.EqualTo(Base + 4 * HourMs));
	}

	private readonly struct ByCreatedAtOnly : IComparer<Order> {
		public int Compare(Order? x, Order? y) => y!.CreatedAtUnixMs.CompareTo(x!.CreatedAtUnixMs);
	}
}
