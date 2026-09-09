namespace Prague.Baseline.Bdn;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using BenchmarkDotNet.Configs;
using Prague.Baseline.Scenario;
using Prague.Core;

// Sort coverage for the tripwire. The query metrics measure lookup, scan and join shapes; nothing
// measured a sort, so a change to the sort plan or to tie handling moved silently. Two axes matter
// and they move independently:
//
//   distinct keys  — Range is random over ProductCount, so comparisons rarely tie.
//   tied keys      — PrimaryValue takes 5 values over 500 rows, so runs of equal rows dominate.
//                    This is the axis a stable sort has to pay for, and the one that was invisible.
//
// Both the classic plan (Sort, materialise + full sort) and the bounded plan (SortBounded, select
// the page) are covered, at a small page and at a page that covers the whole result — the bounded
// plan's routing decision depends on that ratio, so a router change shows up as one metric moving
// while its neighbour does not.
[MemoryDiagnoser]
[Config(typeof(Config))]
public class CoreSortBenchmarks {
	private BaselineProductCache _products = null!;
	private BaselineOfferCache _offers = null!;

	private sealed class Config : ManualConfig {
		public Config() => AddJob(Job.Default
			.WithToolchain(InProcessNoEmitToolchain.Instance)
			.WithWarmupCount(2).WithIterationCount(15));
	}

	// Struct comparers so the sort devirtualises per closed generic, matching how callers are meant
	// to pass them — a class comparer would measure interface dispatch instead of the sort.
	private readonly struct ByRange : IComparer<BaselineProduct> {
		public int Compare(BaselineProduct? a, BaselineProduct? b) => a!.Range.CompareTo(b!.Range);
	}

	private readonly struct ByPrimaryValue : IComparer<BaselineProduct> {
		public int Compare(BaselineProduct? a, BaselineProduct? b) => a!.PrimaryValue.CompareTo(b!.PrimaryValue);
	}

	// The joined row is the sort subject on the joined path, so the comparer has to be over the row.
	private readonly struct ByJoinedPrimaryValue : IComparer<JoinResult<BaselineProduct, QueryResults<BaselineOffer>>> {
		public int Compare(JoinResult<BaselineProduct, QueryResults<BaselineOffer>> a, JoinResult<BaselineProduct, QueryResults<BaselineOffer>> b)
			=> a.Left.PrimaryValue.CompareTo(b.Left.PrimaryValue);
	}

	[GlobalSetup]
	public void Setup() {
		var data = DatasetFactory.Build();
		var registry = new DataCacheRegistryBuilder()
			.Register<BaselineProductCache>()
			.Register<BaselineProductInfoCache>()
			.Register<BaselineOfferCache>()
			.Build();
		_products = registry.GetCache<BaselineProductCache>();
		_offers = registry.GetCache<BaselineOfferCache>();
		var infos = registry.GetCache<BaselineProductInfoCache>();
		foreach (var p in data.Products) _products.AddOrUpdate(p);
		foreach (var i in data.Infos) infos.AddOrUpdate(i);
		foreach (var o in data.Offers) _offers.AddOrUpdate(o);
	}

	// Classic plan, keys that rarely tie — the sort's baseline cost.
	[Benchmark] public int SortDistinct() {
		using var r = _products.Query().Sort(new ByRange()).ExecutePooled();
		return r.Count;
	}

	// Classic plan, 5 distinct keys over 500 rows. Making equal rows keep their encounter order
	// costs real time here and nowhere else, so this is the metric to watch when tie handling changes.
	[Benchmark] public int SortTied() {
		using var r = _products.Query().Sort(new ByPrimaryValue()).ExecutePooled();
		return r.Count;
	}

	// Same tie-heavy keys with a Many join attached: joined rows sort as whole rows rather than as an
	// index array, so they pay for tie handling separately from the simple path.
	[Benchmark] public int SortTiedJoined() {
		using var r = _products.Query()
			.JoinMany(_offers.Cache, _offers.ProductIdIndex)
			.Sort(new ByJoinedPrimaryValue())
			.ExecutePooled();
		return r.Count;
	}

	// The bounded plan on the joined path, at a page far smaller than the result. Its win here is not
	// the sort: join slots are filled for page rows only, so it skips the fill for everything outside
	// the page, and the fill dominates. Paired with SortTiedJoined this says what the bounded plan is
	// actually worth on a joined query.
	[Benchmark] public int SortTiedJoinedBoundedPage() {
		using var r = _products.Query()
			.JoinMany(_offers.Cache, _offers.ProductIdIndex)
			.SortBounded(new ByJoinedPrimaryValue())
			.ExecutePooled(0, 20);
		return r.Count;
	}

	// Sort declared BEFORE the join with a left-value comparer. This is the only joined shape the
	// bounded plan can take: canBound requires SorterOrdersByLeftValues, so a comparer over the joined
	// row falls back to the classic plan without saying so.
	[Benchmark] public int SortTiedLeftThenJoinClassic() {
		using var r = _products.Query()
			.Sort(new ByPrimaryValue())
			.JoinMany(_offers.Cache, _offers.ProductIdIndex)
			.ExecutePooled(0, 20);
		return r.Count;
	}

	[Benchmark] public int SortTiedLeftThenJoinBounded() {
		using var r = _products.Query()
			.SortBounded(new ByPrimaryValue())
			.JoinMany(_offers.Cache, _offers.ProductIdIndex)
			.ExecutePooled(0, 20);
		return r.Count;
	}

	// Bounded plan at a page small enough that selecting beats sorting.
	[Benchmark] public int SortBoundedPage() {
		using var r = _products.Query().SortBounded(new ByPrimaryValue()).ExecutePooled(0, 20);
		return r.Count;
	}

	// Bounded plan at a page that covers every row — selecting buys nothing here, so this is where a
	// plan router would send the query back to the classic sort. Paired with SortBoundedPage it
	// distinguishes "the sort got slower" from "the routing changed".
	[Benchmark] public int SortBoundedFullPage() {
		using var r = _products.Query().SortBounded(new ByPrimaryValue()).ExecutePooled(0, ScenarioSpec.ProductCount);
		return r.Count;
	}
}
