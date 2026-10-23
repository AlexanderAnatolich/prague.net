// docs: every code block of www/docs/articles/core-concepts/query-engine.md — planning, intersection,
// the execute variants, QueryResults, Where, UseIndex, Or, sorting, counting, mapping, cloning, batch
// reads and the query-string parser.
namespace Prague.Samples.Concepts;

using NUnit.Framework;
using Prague.Core;
using Prague.Samples.Models;

[TestFixture]
public class QueryEngine {
	private const long DayMs = 24 * 60 * 60 * 1000L;
	private const long Now = 1_760_000_000_000L;

	private OrderCache _orders = null!;
	private CustomerCache _customers = null!;

	[SetUp]
	public void SetUp() {
		_orders = new OrderCache();
		_orders.AddOrUpdate(NewOrder("ORD-001", 42, Now, OrderStatus.Pending, 150m));
		_orders.AddOrUpdate(NewOrder("ORD-002", 42, Now - DayMs, OrderStatus.Paid, 80m));
		_orders.AddOrUpdate(NewOrder("ORD-003", 42, Now - 2 * DayMs, OrderStatus.Pending, 20m));
		_orders.AddOrUpdate(NewOrder("ORD-004", 42, Now - 5 * DayMs, OrderStatus.Shipped, 300m));
		_orders.AddOrUpdate(NewOrder("ORD-005", 42, Now - 9 * DayMs, OrderStatus.Cancelled, 10m));
		_orders.AddOrUpdate(NewOrder("ORD-006", 7, Now, OrderStatus.Pending, 45m));
		_orders.AddOrUpdate(NewOrder("ORD-007", 9, Now, OrderStatus.Paid, 500m));

		_customers = new CustomerCache();
		_customers.AddOrUpdate(NewCustomer(42, "alice@example.com", "US", CustomerTier.Gold));
		_customers.AddOrUpdate(NewCustomer(7, "bob@example.com", "GB", CustomerTier.Standard));
		_customers.AddOrUpdate(NewCustomer(9, "chen@example.com", "US", CustomerTier.Standard));
		_customers.AddOrUpdate(NewCustomer(11, "dana@example.com", "GB", CustomerTier.Gold));
	}

	[Test]
	public void Anatomy() {
		var yesterdayUnixMs = Now - DayMs;

		#region anatomy
		using var results = _orders.Query()
			.WithCustomerId(42)                                 // Many index lane
			.WithCreatedAtUnixMs(q => q.Gte(yesterdayUnixMs))   // Range index lane
			.ExecutePooled(skip: 0, take: 100);                 // pooled materialization
		#endregion

		Assert.That(Ids(results), Is.EquivalentTo(new[] { "ORD-001", "ORD-002" }));
	}

	[Test]
	public void ExecuteVariants() {
		#region execute-variants
		var allocated = _orders.Query().WithCustomerId(42).Execute();                   // new T[]; nothing to dispose
		var clones = _orders.Query().WithCustomerId(42).ExecuteCloned();                // new T[] of deep clones
		using var pooled = _orders.Query().WithCustomerId(42).ExecutePooled();          // rented buffer: dispose it
		using var pooledClones = _orders.Query().WithCustomerId(42).ExecutePooledCloned();
		var matching = _orders.Query().WithCustomerId(42).Count();                      // no rows materialized
		#endregion

		Assert.That(allocated.Count, Is.EqualTo(5));
		Assert.That(pooled.Count, Is.EqualTo(5));
		Assert.That(pooledClones.Count, Is.EqualTo(5));
		Assert.That(matching, Is.EqualTo(5));

		// Execute hands out the cached instances; the cloned variants hand out equal copies.
		Assert.That(_orders.Cache.TryGet(allocated[0].OrderId, out var stored) && ReferenceEquals(stored, allocated[0]));
		Assert.That(_orders.Cache.TryGet(clones[0].OrderId, out stored) && !ReferenceEquals(stored, clones[0]) && stored!.CacheEquals(clones[0]));
	}

	[Test]
	public void QueryResultsMembers() {
		#region query-results-members
		using var page = _orders.Query().WithCustomerId(42).ExecutePooled(skip: 1, take: 2);
		var onPage = page.Count;                 // rows in this page
		var matched = page.TotalCount;           // rows before paging — "2 of 5"
		ReadOnlySpan<Order> span = page;         // implicit span view; AsSpan() / AsMemory() too
		foreach (ref var order in page)          // ref enumerator: Current is the array slot itself
			Console.WriteLine(order.OrderId);
		var copy = page.ToArray();               // copies out and disposes the pooled buffer
		#endregion

		Assert.That(onPage, Is.EqualTo(2));
		Assert.That(matched, Is.EqualTo(5));
		Assert.That(span.Length, Is.EqualTo(2));
		Assert.That(copy, Has.Length.EqualTo(2));
	}

	[Test]
	public void WhereIsAResidualFilter() {
		#region where-residual
		using var pending = _orders.Query()
			.WithCustomerId(42)                                 // index lane first…
			.Where(o => o.Total > 100m)                         // …then the predicate runs on the survivors only
			.ExecutePooled();
		#endregion

		Assert.That(Ids(pending), Is.EquivalentTo(new[] { "ORD-001", "ORD-004" }));
	}

	[Test]
	public void UseIndexForManualPlanning() {
		#region use-index
		using var gold = _customers.Query().WithTier(CustomerTier.Gold).ExecutePooled();
		using var goldOrders = _orders.Query()
			.UseIndex(_orders.CustomerIdIndex, gold.AsSpan(), static c => c.Id)   // rows of another cache + key selector
			.ExecutePooled();

		ReadOnlySpan<int> customerIds = [42, 7];
		using var byIds = _orders.Query().UseIndex(_orders.CustomerIdIndex, customerIds).ExecutePooled();   // a span of index keys
		using var raw = _orders.Cache.Query().UseIndex(_orders.CustomerIdIndex, 42).ExecutePooled();       // the raw cache has no WithXxx
		#endregion

		Assert.That(goldOrders.Count, Is.EqualTo(5));
		Assert.That(byIds.Count, Is.EqualTo(6));
		Assert.That(raw.Count, Is.EqualTo(5));
	}

	[Test]
	public void OrUnionsTwoBranches() {
		#region or-two-branches
		using var results = _orders.Query()
			.WithStatus(OrderStatus.Pending)
			.Or(
				b => b.WithCustomerId(42),
				b => b.WithCustomerId(7))
			.ExecutePooled();
		// Status = Pending AND (CustomerId = 42 OR CustomerId = 7)
		#endregion

		Assert.That(Ids(results), Is.EquivalentTo(new[] { "ORD-001", "ORD-003", "ORD-006" }));
	}

	[Test]
	public void OrNestsForThreeOrMoreDisjuncts() {
		#region or-nested
		using var results = _orders.Query()
			.Or(
				b => b.WithCustomerId(42),
				b => b.Or(
					c => c.WithCustomerId(7),
					c => c.WithCustomerId(9)))
			.ExecutePooled();
		#endregion

		Assert.That(results.Count, Is.EqualTo(7));
	}

	[Test]
	public void OrAndsWithinABranch() {
		#region or-and-within-branch
		// (Country = US AND Tier = Gold) OR (Country = GB AND Tier = Standard)
		using var results = _customers.Query()
			.Or(
				b => b.WithCountry("US").WithTier(CustomerTier.Gold),
				b => b.WithCountry("GB").WithTier(CustomerTier.Standard))
			.ExecutePooled();
		#endregion

		Assert.That(results.Map(c => c.Id).ToArray(), Is.EquivalentTo(new[] { 42, 7 }));
	}

	[Test]
	public void OrWithStaticLambdasAndAnArgument() {
		#region or-static-arg
		var args = (First: 42, Second: 7);
		using var results = _orders.Query()
			.Or(
				static (b, a) => b.WithCustomerId(a.First),
				static (b, a) => b.WithCustomerId(a.Second),
				args)
			.ExecutePooled();
		#endregion

		Assert.That(results.Count, Is.EqualTo(6));
	}

	[Test]
	public void SortBoundedPage() {
		#region sort-bounded-page
		using var page = _orders.Query()
			.WithCustomerId(42)
			.SortBounded(OrderCache.ByCreatedAtComparer)        // newest first, from [DataCacheSort]
			.ExecutePooled(skip: 0, take: 2);
		#endregion

		Assert.That(Ids(page), Is.EqualTo(new[] { "ORD-001", "ORD-002" }));
		Assert.That(page.TotalCount, Is.EqualTo(5));
	}

	[Test]
	public void CountWithoutMaterializing() {
		#region count
		var n = _orders.Query()
			.WithCustomerId(42)
			.Count();
		#endregion

		Assert.That(n, Is.EqualTo(5));
	}

	[Test]
	public void MapProjectsAfterExecution() {
		#region map
		using var totals = _orders.Query()
			.WithCustomerId(42)
			.ExecutePooled(skip: 1, take: 2)
			.Map(o => (o.OrderId, o.Total));                    // consumes the source; dispose the mapped result
		#endregion

		Assert.That(totals.Count, Is.EqualTo(2));
		Assert.That(totals.TotalCount, Is.EqualTo(5));
	}

	[Test]
	public void DeepCloning() {
		#region deep-clone
		var copy = _orders.Query().WithOrderId("ORD-001").Execute()[0].Clone();   // generated: recursive, collection-aware

		using var rows = _orders.Query().WithCustomerId(42).ExecutePooled();
		rows.CloneElements();                                                       // clone every row in place, after the fact
		#endregion

		Assert.That(_orders.Cache.TryGet("ORD-001", out var stored));
		Assert.That(copy, Is.Not.SameAs(stored));
		Assert.That(copy.CacheEquals(stored));
		foreach (var row in rows)
			Assert.That(_orders.Cache.TryGet(row.OrderId, out var resident) && !ReferenceEquals(resident, row));
	}

	[Test]
	public void BatchReads() {
		#region batch-reads
		var keys = new[] { "ORD-001", "ORD-002", "ORD-404" };

		Span<Order> buffer = new Order[keys.Length];
		var found = _orders.Cache.TryGet(keys, buffer);                   // fills the span, returns the hit count

		var orders = _orders.Cache.TryGet(keys);                          // List<Order>: only the hits
		var big = _orders.Cache.TryGet(keys, o => o.Total > 100m);        // hits that also pass the predicate
		#endregion

		Assert.That(found, Is.EqualTo(2));
		Assert.That(orders, Has.Count.EqualTo(2));
		Assert.That(big.Select(o => o.OrderId), Is.EquivalentTo(new[] { "ORD-001" }));
	}

	[Test]
	public void StringQuery() {
		#region string-query
		using var results = _orders.QueryParser.StringQueryPooled("customerId=42&total>100");   // names are case-insensitive
		var fields = _orders.QueryParser.QueryableFields;                                        // what a client may ask for
		#endregion

		Assert.That(Ids(results), Is.EquivalentTo(new[] { "ORD-001", "ORD-004" }));
		Assert.That(fields.Select(f => f.Name), Does.Contain("CustomerId").And.Contain("Total"));
	}

	private static string[] Ids(QueryResults<Order> orders) {
		var ids = new string[orders.Count];
		for (var i = 0; i < ids.Length; i++)
			ids[i] = orders[i].OrderId;
		return ids;
	}

	private static Order NewOrder(string orderId, int customerId, long createdAtUnixMs, OrderStatus status, decimal total) => new() {
		OrderId = orderId,
		CustomerId = customerId,
		CreatedAtUnixMs = createdAtUnixMs,
		Status = status,
		Total = total,
	};

	private static Customer NewCustomer(int id, string email, string country, CustomerTier tier) => new() {
		Id = id,
		Email = email,
		Country = country,
		Tier = tier,
		Name = email[..email.IndexOf('@')],
	};
}
