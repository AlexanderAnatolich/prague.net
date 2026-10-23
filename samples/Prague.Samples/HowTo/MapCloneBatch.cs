// docs: every code block of www/docs/articles/how-to/map-clone-batch.md — projecting results with Map, the
// generated Clone() and the *Cloned terminals, batch reads by key, and what each one allocates.
namespace Prague.Samples.HowTo;

using NUnit.Framework;
using Prague.Core;
using Prague.Samples.Models;

public sealed record OrderTotal(string OrderId, decimal Total);

[TestFixture]
public class MapCloneBatch {
	private OrderCache _orders = null!;

	[SetUp]
	public void SetUp() {
		_orders = new OrderCache();
		_orders.AddOrUpdate(NewOrder("ORD-001", 42, 120m));
		_orders.AddOrUpdate(NewOrder("ORD-002", 42, 80m));
		_orders.AddOrUpdate(NewOrder("ORD-003", 7, 60m));
	}

	[Test]
	public void MapProjectsAndConsumesTheSource() {
		var orders = _orders;

		#region map-results
		using var totals = orders.Query()
			.WithCustomerId(42)
			.ExecutePooled(skip: 0, take: 10)
			.Map(o => new OrderTotal(o.OrderId, o.Total));     // same pooled kind; the Order buffer is disposed for you

		Console.WriteLine($"{totals.Count} of {totals.TotalCount}");   // TotalCount survives the projection
		#endregion

		Assert.That(totals.Select(t => t.OrderId), Is.EquivalentTo(new[] { "ORD-001", "ORD-002" }));
		Assert.That(totals.TotalCount, Is.EqualTo(2));
	}

	[Test]
	public void ConversionsConsumeToo() {
		var orders = _orders;

		#region map-conversions
		var list = orders.Query().WithCustomerId(42).ExecutePooled().ToList();        // copies out, returns the buffer
		var byId = orders.Query().WithCustomerId(42).ExecutePooled().ToDictionary(o => o.OrderId);
		var array = orders.Query().WithCustomerId(42).Execute().ToArray();            // non-pooled, whole buffer: no copy

		using var rows = orders.Query().WithCustomerId(42).ExecutePooled();
		ReadOnlySpan<Order> span = rows;                                              // no copy; valid until Dispose
		#endregion

		Assert.That(list, Has.Count.EqualTo(2));
		Assert.That(byId["ORD-001"].Total, Is.EqualTo(120m));
		Assert.That(array, Has.Length.EqualTo(2));
		Assert.That(span.Length, Is.EqualTo(2));
	}

	[Test]
	public void CloneIsDeepAndEqual() {
		var orders = _orders;
		orders.Cache.TryGet("ORD-001", out var resident);

		#region clone-generated
		var copy = resident!.Clone();                           // generated: recursive, collection-aware, no reflection

		var sameContent = copy.CacheEquals(resident);           // true
		var sameInstance = ReferenceEquals(copy, resident);     // false: mutate the copy, the cache never sees it
		#endregion

		Assert.That(sameContent, Is.True);
		Assert.That(sameInstance, Is.False);
	}

	[Test]
	public void ClonedTerminalsHandOutCopies() {
		var orders = _orders;
		orders.Cache.TryGet("ORD-001", out var resident);

		#region clone-terminals
		using var shared = orders.Query().WithOrderId("ORD-001").ExecutePooled();         // rows ARE the cache's instances
		using var copies = orders.Query().WithOrderId("ORD-001").ExecutePooledCloned();   // every row cloned on the way out

		using var later = orders.Query().WithOrderId("ORD-001").ExecutePooled();
		later.CloneElements();                                  // or clone in place, after the fact
		#endregion

		Assert.That(ReferenceEquals(shared[0], resident), Is.True);
		Assert.That(ReferenceEquals(copies[0], resident), Is.False);
		Assert.That(ReferenceEquals(later[0], resident), Is.False);
		Assert.That(copies[0].CacheEquals(resident), Is.True);
	}

	[Test]
	public void BatchReadsByKey() {
		var orders = _orders;

		#region batch-reads
		string[] keys = ["ORD-001", "ORD-003", "ORD-999"];

		Span<Order> buffer = new Order[keys.Length];
		var found = orders.Cache.TryGet(keys, buffer);           // fills the span with the hits, returns how many

		var hits = orders.Cache.TryGet(keys);                    // List<Order> of the found items only
		var big = orders.Cache.TryGet(keys, o => o.Total > 100m);   // ... that also pass the predicate

		if (orders.Cache.TryGet("ORD-002", out var one))        // single key: no allocation
			Console.WriteLine(one.Total);
		#endregion

		Assert.That(found, Is.EqualTo(2));
		Assert.That(buffer[..found].ToArray().Select(o => o.OrderId), Is.EquivalentTo(new[] { "ORD-001", "ORD-003" }));
		Assert.That(hits, Has.Count.EqualTo(2));
		Assert.That(big.Single().OrderId, Is.EqualTo("ORD-001"));
		Assert.That(one!.Total, Is.EqualTo(80m));
	}

	[Test]
	public void BatchReadThroughTheKeyLane() {
		var orders = _orders;

		#region batch-with-key
		string[] keys = ["ORD-001", "ORD-003"];
		using var rows = orders.Query()
			.WithKey(keys)                                      // multi-value primary-key lane (OR within the lane)
			.Where(o => o.Status == OrderStatus.Paid)
			.ExecutePooled();
		#endregion

		Assert.That(rows.Select(o => o.OrderId), Is.EquivalentTo(keys));
	}

	private static Order NewOrder(string orderId, int customerId, decimal total) => new() {
		OrderId = orderId,
		CustomerId = customerId,
		CreatedAtUnixMs = 1_000,
		Status = OrderStatus.Paid,
		Total = total,
	};
}
