namespace Prague.Benchmarks;

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using Core;

/// <summary>
///   Fan-out JoinMany benchmarks for the two resolver families that group many lefts under one
///   lookup key: the left-symmetric family (a <c>CacheSymmetricKeyValueListIndex</c> on the left
///   joined to a <c>CacheKeyValueListIndex</c> on the right) and the M:N collection family
///   (a <c>CacheCollectionSymmetricKeyValueListIndex</c> over the right's element list).
///   Every query covers ALL lefts and is executed through the public query API — the pooled
///   <c>ExecutePooled()</c> + <c>Dispose()</c> path, plus one allocating <c>Execute()</c> variant.
///
///   Left-symmetric shape: <c>Groups</c> lookup keys, <c>groupSize</c> lefts and <c>rightsPerGroup</c>
///   rights per key — every left in a group receives the whole group's rights.
///   Collection shape: <c>Owners</c> right rows, each holding <c>elementsPerOwner</c> distinct element
///   ids drawn from as many lefts as there are owners, so each left is shared by ~<c>elementsPerOwner</c>
///   owners on average.
///
///   Cases are declared with per-method <c>[Arguments]</c> rather than class-level <c>[Params]</c>: the
///   shapes are independent, and a shared parameter set would run the meaningless cross-product.
///   All datasets are built once in <c>GlobalSetup</c> (fixed seed) and selected by argument.
///
///   Run: dotnet run -c Release --project benchmarks/Prague.Benchmarks -- --filter "*JoinManyLeftSymCollectionBenchmarks*"
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[Orderer(SummaryOrderPolicy.Declared)]
public class JoinManyLeftSymCollectionBenchmarks {
	private const int Groups = 16;
	private const int Owners = 512;
	private const int LargeGroups = 1024;
	private const int LargeOwners = 32768;
	private const int Seed = 42;

	private static readonly int[] GroupSizes = { 1, 8, 64 };
	private static readonly int[] RightsPerGroupSizes = { 8, 64 };
	private static readonly int[] ElementsPerOwnerSizes = { 1, 3, 8 };

	private LeftSymSet[] _leftSymSets = null!;
	private CollectionSet[] _collectionSets = null!;
	private LeftSymSet _leftSymLarge = null!;
	private CollectionSet _collectionLarge = null!;

	[GlobalSetup]
	public void Setup() {
		_leftSymSets = new LeftSymSet[GroupSizes.Length * RightsPerGroupSizes.Length];
		for (var gi = 0; gi < GroupSizes.Length; gi++) {
			for (var ri = 0; ri < RightsPerGroupSizes.Length; ri++) {
				_leftSymSets[gi * RightsPerGroupSizes.Length + ri] = BuildLeftSym(Groups, GroupSizes[gi], RightsPerGroupSizes[ri]);
			}
		}

		_collectionSets = new CollectionSet[ElementsPerOwnerSizes.Length];
		for (var ki = 0; ki < ElementsPerOwnerSizes.Length; ki++) {
			_collectionSets[ki] = BuildCollection(Owners, ElementsPerOwnerSizes[ki]);
		}

		_leftSymLarge = BuildLeftSym(LargeGroups, 8, 64);
		_collectionLarge = BuildCollection(LargeOwners, 8);
	}

	// ── Left-symmetric: outer, pooled ─────────────────────────────────────────

	[Benchmark]
	[Arguments(1, 8)]
	[Arguments(1, 64)]
	[Arguments(8, 8)]
	[Arguments(8, 64)]
	[Arguments(64, 8)]
	[Arguments(64, 64)]
	public int LeftSym_Outer_Pooled(int groupSize, int rightsPerGroup) {
		var set = LeftSym(groupSize, rightsPerGroup);
		using var r = set.Lefts.Query().JoinMany(set.LeftGroupSym, set.Rights, set.RightGroupIdx).ExecutePooled();
		return r.Count;
	}

	// ── Left-symmetric: outer, allocating ─────────────────────────────────────

	[Benchmark]
	[Arguments(8, 64)]
	public int LeftSym_Outer_Execute(int groupSize, int rightsPerGroup) {
		var set = LeftSym(groupSize, rightsPerGroup);
		var r = set.Lefts.Query().JoinMany(set.LeftGroupSym, set.Rights, set.RightGroupIdx).Execute();
		return r.Count;
	}

	// ── Left-symmetric: outer with a right-side predicate, pooled ─────────────

	[Benchmark]
	[Arguments(8, 64)]
	public int LeftSym_OuterFiltered_Pooled(int groupSize, int rightsPerGroup) {
		var set = LeftSym(groupSize, rightsPerGroup);
		using var r = set.Lefts.Query()
			.JoinMany(set.LeftGroupSym, set.Rights, set.RightGroupIdx, q => q.Where(x => x.Flag))
			.ExecutePooled();
		return r.Count;
	}

	// ── Left-symmetric: inner, pooled ─────────────────────────────────────────

	[Benchmark]
	[Arguments(8, 64)]
	public int LeftSym_Inner_Pooled(int groupSize, int rightsPerGroup) {
		var set = LeftSym(groupSize, rightsPerGroup);
		using var r = set.Lefts.Query().InnerJoinMany(set.LeftGroupSym, set.Rights, set.RightGroupIdx).ExecutePooled();
		return r.Count;
	}

	// ── Collection: outer, pooled ─────────────────────────────────────────────

	[Benchmark]
	[Arguments(1)]
	[Arguments(3)]
	[Arguments(8)]
	public int Collection_Outer_Pooled(int elementsPerOwner) {
		var set = Collection(elementsPerOwner);
		using var r = set.Elements.Query().JoinManyCollection(set.Owners, set.OwnerElementsIdx).ExecutePooled();
		return r.Count;
	}

	// ── Collection: inner, pooled ─────────────────────────────────────────────

	[Benchmark]
	[Arguments(3)]
	public int Collection_Inner_Pooled(int elementsPerOwner) {
		var set = Collection(elementsPerOwner);
		using var r = set.Elements.Query().InnerJoinManyCollection(set.Owners, set.OwnerElementsIdx).ExecutePooled();
		return r.Count;
	}

	// ── Large working sets: the per-query right set no longer fits the L2 cache ──────────
	//    LeftSym: 1024 groups x 8 lefts x 64 rights = 524288 delivered rows over 65536 rights.
	//    Collection: 32768 owners x 8 elements over 32768 elements.

	[Benchmark]
	public int LeftSym_Outer_Pooled_Large() {
		var set = _leftSymLarge;
		using var r = set.Lefts.Query().JoinMany(set.LeftGroupSym, set.Rights, set.RightGroupIdx).ExecutePooled();
		return r.Count;
	}

	[Benchmark]
	public int Collection_Outer_Pooled_Large() {
		var set = _collectionLarge;
		using var r = set.Elements.Query().JoinManyCollection(set.Owners, set.OwnerElementsIdx).ExecutePooled();
		return r.Count;
	}

	// ── Dataset selection ─────────────────────────────────────────────────────

	private LeftSymSet LeftSym(int groupSize, int rightsPerGroup) =>
		_leftSymSets[IndexOf(GroupSizes, groupSize) * RightsPerGroupSizes.Length + IndexOf(RightsPerGroupSizes, rightsPerGroup)];

	private CollectionSet Collection(int elementsPerOwner) => _collectionSets[IndexOf(ElementsPerOwnerSizes, elementsPerOwner)];

	private static int IndexOf(int[] sizes, int value) {
		for (var i = 0; i < sizes.Length; i++) {
			if (sizes[i] == value) {
				return i;
			}
		}

		throw new ArgumentOutOfRangeException(nameof(value), value, "Benchmark argument has no prebuilt dataset.");
	}

	// ── Dataset construction ──────────────────────────────────────────────────

	private static LeftSymSet BuildLeftSym(int groups, int groupSize, int rightsPerGroup) {
		var random = new Random(Seed);
		var set = new LeftSymSet();

		// Left i belongs to group i % groups, so every group has exactly groupSize lefts.
		var leftCount = groups * groupSize;
		for (var i = 0; i < leftCount; i++) {
			var id = i + 1;
			set.Lefts.AddOrUpdate(id, new BmLsLeft { Id = id, Group = i % groups });
		}

		// Right j belongs to group j % groups, so every group has exactly rightsPerGroup rights;
		// Flag is a coin toss the filtered variant selects on (~half of each group's rights).
		var rightCount = groups * rightsPerGroup;
		for (var j = 0; j < rightCount; j++) {
			var id = j + 1;
			set.Rights.AddOrUpdate(id, new BmLsRight { Id = id, Group = j % groups, Flag = random.Next(2) == 0 });
		}

		return set;
	}

	private static CollectionSet BuildCollection(int owners, int elementsPerOwner) {
		var random = new Random(Seed);
		var set = new CollectionSet();

		// As many elements (lefts) as owners, so each element lands in ~elementsPerOwner owners.
		for (var e = 1; e <= owners; e++) {
			set.Elements.AddOrUpdate(e, new BmCoElement { Id = e });
		}

		// Each owner references elementsPerOwner distinct random elements.
		var picked = new bool[owners + 1];
		for (var o = 1; o <= owners; o++) {
			var ids = new List<int>(elementsPerOwner);
			while (ids.Count < elementsPerOwner) {
				var e = random.Next(1, owners + 1);
				if (picked[e]) {
					continue;
				}

				picked[e] = true;
				ids.Add(e);
			}

			for (var i = 0; i < ids.Count; i++) {
				picked[ids[i]] = false;
			}

			set.Owners.AddOrUpdate(o, new BmCoOwner { Id = o, ElementIds = ids });
		}

		return set;
	}

	private sealed class LeftSymSet {
		public readonly InMemoryDataCache<int, BmLsLeft> Lefts = new();
		public readonly InMemoryDataCache<int, BmLsRight> Rights = new();
		public readonly CacheSymmetricKeyValueListIndex<int, BmLsLeft, int> LeftGroupSym;
		public readonly CacheKeyValueListIndex<int, BmLsRight, int> RightGroupIdx;

		public LeftSymSet() {
			LeftGroupSym = Lefts.CacheSymmetricKeyValueListIndex<int>((_, v) => v.Group);
			RightGroupIdx = Rights.CacheKeyValueListIndex<int>((_, v) => v.Group);
		}
	}

	private sealed class CollectionSet {
		public readonly InMemoryDataCache<int, BmCoElement> Elements = new();
		public readonly InMemoryDataCache<int, BmCoOwner> Owners = new();
		public readonly CacheCollectionSymmetricKeyValueListIndex<int, BmCoOwner, int> OwnerElementsIdx;

		public CollectionSet() {
			OwnerElementsIdx = Owners.CacheCollectionSymmetricKeyValueListIndex<int>((_, o) => o.ElementIds);
		}
	}

	// ── Models ────────────────────────────────────────────────────────────────

	internal sealed class BmLsLeft : ICacheEquatable<BmLsLeft>, ICacheClonable<BmLsLeft> {
		public int Id { get; init; }
		public int Group { get; init; }
		public bool CacheEquals(BmLsLeft? other) => other is not null && other.Id == Id && other.Group == Group;
		public int CacheGetHashCode() => HashCode.Combine(Id, Group);
		public BmLsLeft Clone() => new() { Id = Id, Group = Group };
	}

	internal sealed class BmLsRight : ICacheEquatable<BmLsRight>, ICacheClonable<BmLsRight> {
		public int Id { get; init; }
		public int Group { get; init; }
		public bool Flag { get; init; }
		public bool CacheEquals(BmLsRight? other) => other is not null && other.Id == Id && other.Group == Group && other.Flag == Flag;
		public int CacheGetHashCode() => HashCode.Combine(Id, Group, Flag);
		public BmLsRight Clone() => new() { Id = Id, Group = Group, Flag = Flag };
	}

	internal sealed class BmCoElement : ICacheEquatable<BmCoElement>, ICacheClonable<BmCoElement> {
		public int Id { get; init; }
		public bool CacheEquals(BmCoElement? other) => other is not null && other.Id == Id;
		public int CacheGetHashCode() => Id;
		public BmCoElement Clone() => new() { Id = Id };
	}

	internal sealed class BmCoOwner : ICacheEquatable<BmCoOwner>, ICacheClonable<BmCoOwner> {
		public int Id { get; init; }
		public List<int> ElementIds { get; init; } = new();
		public bool CacheEquals(BmCoOwner? other) => other is not null && other.Id == Id && other.ElementIds.SequenceEqual(ElementIds);
		public int CacheGetHashCode() => HashCode.Combine(Id, ElementIds.Count);
		public BmCoOwner Clone() => new() { Id = Id, ElementIds = new List<int>(ElementIds) };
	}
}
