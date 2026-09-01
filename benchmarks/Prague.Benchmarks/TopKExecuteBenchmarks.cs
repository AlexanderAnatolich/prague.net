namespace Prague.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Prague.Core;

/// <summary>
///   Sorted + paged queries: the classic full-materialization pipeline (reached through the
///   internal cores — the public sorted terminals now bound transparently) vs the public
///   ExecutePooled(skip, take), which selects the page with a heap of size skip+take.
///
///   Classic: Init(candidateCount) rents a buffer for EVERY matched row, adds them all,
///   sorts all N, then slices to `take`. Cost scales with N regardless of take.
///
///   Bounded: the base walk pushes each matched row through a heap of size K = skip+take,
///   drains K rows ascending, and materializes only those. Cost scales with K.
///
///   Both simple (no joins) and joined (InnerJoinOne) shapes are measured — the joined
///   shape is the expensive one, since the classic path also sizes a ValueDictionary of
///   fat JoinResult rows to the pre-join candidate count.
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class TopKExecuteBenchmarks {
	[Params(10_000, 100_000, 500_000)]
	public int N { get; set; }

	[Params(20, 200)]
	public int Take { get; set; }

	private InMemoryDataCache<int, TkbLeft> _left = null!;
	private InMemoryDataCache<int, TkbRight> _right = null!;
	private readonly TkbByScoreAsc _comparer = new();

	[GlobalSetup]
	public void Setup() {
		_left = new InMemoryDataCache<int, TkbLeft>();
		_right = new InMemoryDataCache<int, TkbRight>();
		var rng = new Random(1234);
		for (var i = 0; i < N; i++) {
			_left.AddOrUpdate(i, new TkbLeft { Id = i, Score = rng.Next(int.MaxValue) });
			_right.AddOrUpdate(i, new TkbRight { Id = i, Label = "r" });
		}
	}

	[Benchmark(Baseline = true)]
	public int Sorted_ClassicCore_Pooled() {
		var q = _left.Query().Sort(_comparer);
		using var results = q.ExecuteCoreSimple(ref q._resolverChain.Resolver, true, false, 0, Take);
		return results.Count;
	}

	[Benchmark]
	public int Sorted_ExecutePooled() {
		using var results = _left.Query().Sort(_comparer).ExecutePooled(0, Take);
		return results.Count;
	}

	[Benchmark]
	public int SortedInnerJoin_ClassicCore_Pooled() {
		var q = _left.Query().Sort(_comparer).InnerJoinOne(_right);
		using var results = q.ExecuteCoreJoined<JoinResult<TkbLeft, TkbRight>>(true, false, 0, Take);
		return results.Count;
	}

	[Benchmark]
	public int SortedInnerJoin_ExecutePooled() {
		using var results = _left.Query().Sort(_comparer).InnerJoinOne(_right).ExecutePooled(0, Take);
		return results.Count;
	}
}

public sealed class TkbLeft : ICacheEquatable<TkbLeft>, ICacheClonable<TkbLeft> {
	public int Id { get; init; }
	public int Score { get; init; }
	public bool CacheEquals(TkbLeft? other) => other is not null && other.Id == Id && other.Score == Score;
	public int CacheGetHashCode() => HashCode.Combine(Id, Score);
	public TkbLeft Clone() => new() { Id = Id, Score = Score };
}

public sealed class TkbRight : ICacheEquatable<TkbRight>, ICacheClonable<TkbRight> {
	public int Id { get; init; }
	public string Label { get; init; } = "";
	public bool CacheEquals(TkbRight? other) => other is not null && other.Id == Id && other.Label == Label;
	public int CacheGetHashCode() => HashCode.Combine(Id, Label);
	public TkbRight Clone() => new() { Id = Id, Label = Label };
}

public sealed class TkbByScoreAsc : IComparer<TkbLeft> {
	public int Compare(TkbLeft? x, TkbLeft? y) => (x?.Score ?? 0).CompareTo(y?.Score ?? 0);
}
