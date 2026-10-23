// docs: every code block of www/docs/articles/core-concepts/prepared-queries.md — a frozen query in a
// field, Build versus BuildFrozen, the arguments struct, the in-arg Where, Match / If branching,
// Explain and FrozenOptions.
namespace Prague.Samples.Concepts;

using NUnit.Framework;
using Prague.Core;
using Prague.Samples.Models;

#region frozen-in-a-field
public sealed class CustomerOrdersPage {
	private readonly FrozenQuery<(int CustomerId, OrderStatus Status), JoinResult<Order, Customer?>> _page;

	public CustomerOrdersPage(OrderCache orders) {
		_page = orders.Prepare<(int CustomerId, OrderStatus Status)>()
			.WithCustomerId(static a => a.CustomerId)
			.WithStatus(static a => a.Status)
			.SortBounded(OrderCache.ByCreatedAtComparer)
			.JoinWithCustomer()
			.BuildFrozen();                                               // the only allocation
	}

	public int CountPage(int customerId, OrderStatus status) {
		using var page = _page.ExecutePooled((customerId, status), skip: 0, take: 20);   // 0 B, any thread
		return page.Count;
	}
}
#endregion

#region lookup-args
public enum LookupMode {
	ByCustomer,
	ByStatus,
}

public readonly record struct OrderLookup(LookupMode Mode, int CustomerId, OrderStatus Status, decimal MinTotal, bool SettledOnly);
#endregion

[TestFixture]
public class PreparedQueries {
	private const long DayMs = 24 * 60 * 60 * 1000L;
	private const long Now = 1_760_000_000_000L;

	private DataCacheRegistry _registry = null!;
	private OrderCache _orders = null!;

	[SetUp]
	public void SetUp() {
		_registry = new DataCacheRegistryBuilder()
			.Register<OrderCache>()
			.Register<CustomerCache>()
			.Register<OrderLineCache>()
			.Register<ShipmentCache>()
			.Register<ProductCache>()
			.Build();
		_orders = _registry.GetCache<OrderCache>();
		var customers = _registry.GetCache<CustomerCache>();

		customers.AddOrUpdate(new Customer { Id = 42, Email = "alice@example.com", Country = "US", Tier = CustomerTier.Gold, Name = "alice" });
		_orders.AddOrUpdate(NewOrder("ORD-001", 42, Now, OrderStatus.Pending, 150m, settledAtUnixMs: null));
		_orders.AddOrUpdate(NewOrder("ORD-002", 42, Now - DayMs, OrderStatus.Pending, 80m, settledAtUnixMs: Now));
		_orders.AddOrUpdate(NewOrder("ORD-003", 42, Now - 2 * DayMs, OrderStatus.Paid, 20m, settledAtUnixMs: Now));
		_orders.AddOrUpdate(NewOrder("ORD-004", 7, Now, OrderStatus.Pending, 300m, settledAtUnixMs: null));
	}

	[Test]
	public void FrozenQueryInAField() {
		var page = new CustomerOrdersPage(_orders);

		Assert.That(page.CountPage(42, OrderStatus.Pending), Is.EqualTo(2));
		Assert.That(page.CountPage(42, OrderStatus.Paid), Is.EqualTo(1));
		Assert.That(page.CountPage(7, OrderStatus.Cancelled), Is.Zero);
	}

	[Test]
	public void BuildVersusBuildFrozen() {
		#region build-vs-build-frozen
		var replayed = _orders.Prepare<int>().WithCustomerId(static a => a).Build();         // PreparedQuery: replays the description
		var frozen = _orders.Prepare<int>().WithCustomerId(static a => a).BuildFrozen();     // FrozenQuery: pre-bound executor

		using var fromReplay = replayed.ExecutePooled(42);
		using var fromFrozen = frozen.ExecutePooled(42);
		#endregion

		Assert.That(Ids(fromFrozen), Is.EquivalentTo(Ids(fromReplay)));                     // same rows; order is the frozen query's own
		Assert.That(frozen.Count(42), Is.EqualTo(replayed.Count(42)));
	}

	[Test]
	public void ArgWherePredicateTakesTheArgumentsByIn() {
		#region arg-where-in
		var expensive = _orders.Prepare<(int CustomerId, decimal MinTotal)>()
			.WithCustomerId(static a => a.CustomerId)
			.Where(static (o, in a) => o.Total >= a.MinTotal)   // the arg predicate runs per row: TArgs by in, spelled out
			.BuildFrozen();
		#endregion

		using var rows = expensive.ExecutePooled((42, 100m));

		Assert.That(Ids(rows), Is.EquivalentTo(new[] { "ORD-001" }));
	}

	[Test]
	public void MatchOnATag() {
		#region match-tag
		// Tag form — a switch: one selector call per execution, then constant compares; first equal tag wins.
		var byMode = _orders.Prepare<OrderLookup>()
			.Match(static a => a.Mode, m => m
				.Case(LookupMode.ByCustomer, b => b.WithCustomerId(static a => a.CustomerId))
				.Case(LookupMode.ByStatus, b => b.WithStatus(static a => a.Status))
				.Default(b => b.Where(static o => o.Total >= 100m)))
			.BuildFrozen();
		#endregion

		using var byCustomer = byMode.ExecutePooled(new OrderLookup(LookupMode.ByCustomer, 7, default, 0, false));
		using var byStatus = byMode.ExecutePooled(new OrderLookup(LookupMode.ByStatus, 0, OrderStatus.Paid, 0, false));
		using var fallback = byMode.ExecutePooled(new OrderLookup((LookupMode)99, 0, default, 0, false));

		Assert.That(Ids(byCustomer), Is.EquivalentTo(new[] { "ORD-004" }));
		Assert.That(Ids(byStatus), Is.EquivalentTo(new[] { "ORD-003" }));
		Assert.That(Ids(fallback), Is.EquivalentTo(new[] { "ORD-001", "ORD-004" }));
	}

	[Test]
	public void MatchOnGuards() {
		#region match-guard
		// Guard form — if / else if / else: no selector; the guards run in order until one is true.
		var guarded = _orders.Prepare<OrderLookup>()
			.WithCustomerId(static a => a.CustomerId)
			.Match(m => m
				.Case(static a => a.MinTotal > 0, b => b.Where(static (o, in a) => o.Total >= a.MinTotal))
				.Case(static a => a.SettledOnly, b => b.WithSettledAtUnixMs())
				.Default())                                        // "nothing matched, narrow nothing", stated
			.BuildFrozen();
		#endregion

		using var minTotal = guarded.ExecutePooled(new OrderLookup(default, 42, default, 100m, SettledOnly: true));
		using var settled = guarded.ExecutePooled(new OrderLookup(default, 42, default, 0, SettledOnly: true));
		using var everything = guarded.ExecutePooled(new OrderLookup(default, 42, default, 0, SettledOnly: false));

		Assert.That(Ids(minTotal), Is.EquivalentTo(new[] { "ORD-001" }), "the first true guard wins");
		Assert.That(Ids(settled), Is.EquivalentTo(new[] { "ORD-002", "ORD-003" }));
		Assert.That(everything.Count, Is.EqualTo(3));
	}

	[Test]
	public void IfAndIfElse() {
		#region if-else
		var settledOnly = _orders.Prepare<OrderLookup>()
			.WithCustomerId(static a => a.CustomerId)
			.If(static a => a.SettledOnly, b => b.WithSettledAtUnixMs())
			.BuildFrozen();

		var either = _orders.Prepare<OrderLookup>()
			.IfElse(static a => a.SettledOnly,
				b => b.WithSettledAtUnixMs(),
				b => b.WithStatus(static a => a.Status))
			.BuildFrozen();
		#endregion

		Assert.That(settledOnly.Count(new OrderLookup(default, 42, default, 0, SettledOnly: true)), Is.EqualTo(2));
		Assert.That(settledOnly.Count(new OrderLookup(default, 42, default, 0, SettledOnly: false)), Is.EqualTo(3));
		Assert.That(either.Count(new OrderLookup(default, 0, OrderStatus.Pending, 0, SettledOnly: true)), Is.EqualTo(2));
		Assert.That(either.Count(new OrderLookup(default, 0, OrderStatus.Pending, 0, SettledOnly: false)), Is.EqualTo(3));
	}

	[Test]
	public void ExplainPrintsThePlan() {
		#region explain
		var frozen = _orders.Prepare<(int CustomerId, OrderStatus Status)>()
			.WithCustomerId(static a => a.CustomerId)
			.WithStatus(static a => a.Status)
			.BuildFrozen();

		Console.WriteLine(frozen.Explain());                   // executor, seed rule, steps, filters
		#endregion

		Assert.That(frozen.Explain(), Does.Contain("executor: Pipeline").And.Contain("steps: [0 ListEq probe: value-side, 1 ListEq probe: value-side]"));
	}

	[Test]
	public void PreserveEagerOrder() {
		#region preserve-eager-order
		var eagerOrder = _orders.Prepare<(int CustomerId, OrderStatus Status)>()
			.WithCustomerId(static a => a.CustomerId)
			.WithStatus(static a => a.Status)
			.BuildFrozen(new FrozenOptions { PreserveEagerOrder = true });
		#endregion

		using var frozenRows = eagerOrder.ExecutePooled((42, OrderStatus.Pending));
		using var eagerRows = _orders.Query().WithCustomerId(42).WithStatus(OrderStatus.Pending).ExecutePooled();

		Assert.That(Ids(frozenRows), Is.EqualTo(Ids(eagerRows)), "byte-identical to the eager sequence");
		Assert.That(eagerOrder.Explain(), Does.Contain("fixed: first active step"));
	}

	private static string[] Ids(QueryResults<Order> orders) {
		var ids = new string[orders.Count];
		for (var i = 0; i < ids.Length; i++)
			ids[i] = orders[i].OrderId;
		return ids;
	}

	private static Order NewOrder(string orderId, int customerId, long createdAtUnixMs, OrderStatus status, decimal total, long? settledAtUnixMs) => new() {
		OrderId = orderId,
		CustomerId = customerId,
		CreatedAtUnixMs = createdAtUnixMs,
		Status = status,
		SettledAtUnixMs = settledAtUnixMs,
		Total = total,
	};
}
