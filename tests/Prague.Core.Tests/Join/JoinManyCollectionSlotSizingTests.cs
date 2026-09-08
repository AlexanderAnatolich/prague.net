namespace Prague.Core.Tests.Join;

using System.Collections.Generic;
using Prague.Core;
using Prague.Core.TypeSystem;
using NUnit.Framework;

/// <summary>
///   <b>These tests document a known defect, so they assert the broken numbers.</b>
///
///   <see cref="JoinManyCollectionResolver{TLeftKey,TLeftValue,TRightCache,TRightKey,TRightValue,TOwnerValue,TFilter}"/>
///   sizes each left's slot from a live <c>rights.Count</c> and then enumerates that same set to
///   build the pairs — the race the right-list resolver was fixed for — and on top of that delivers
///   rows by fanning each right out over a live <c>LeftKeySetView</c> of the owners at Add time. So
///   a slot can be over-delivered two ways: an element added to the walked collection between the
///   count and the walk, and an owner gaining the element after its own slot was sized.
///
///   <see cref="QueryResults{T}.UnsafeAdd"/> drops the surplus rather than failing the query, so
///   this is lossy, never fatal (see context/joins.md, "Concurrency"). Fixing the sizing is #59's
///   job; when it lands, the reserved and delivered counts below become equal and these tests fail
///   — that is the point of them.
/// </summary>
[TestFixture]
public class JoinManyCollectionSlotSizingTests {
	private InMemoryDataCache<int, MnTag> _tagCache = null!;
	private InMemoryDataCache<int, MnTaggedBook> _bookCache = null!;
	private CacheCollectionSymmetricKeyValueListIndex<int, MnTaggedBook, int> _index = null!;

	[SetUp]
	public void SetUp() {
		_tagCache = new InMemoryDataCache<int, MnTag>();
		_bookCache = new InMemoryDataCache<int, MnTaggedBook>();
		_index = _bookCache.CacheCollectionSymmetricKeyValueListIndex<int>((_, b) => b.TagIds);
	}

	// Deterministic: the container's Init hook stands in for a writer that gives book 2 tag 10 after
	// tag 10's slot has been sized for one book. Tag 10's own walk then finds two, and both fan out
	// back to tag 10.
	[Test]
	public void ExecuteOuter_OwnerGainsAnElementAfterSlotSized_OverfillsThatOwnersSlot() {
		_tagCache.AddOrUpdate(10, new MnTag { Id = 10, Name = "fantasy" });
		_tagCache.AddOrUpdate(20, new MnTag { Id = 20, Name = "classic" });
		_bookCache.AddOrUpdate(1, new MnTaggedBook { Id = 1, Title = "Hobbit", TagIds = new List<int> { 10, 20 } });
		_bookCache.AddOrUpdate(2, new MnTaggedBook { Id = 2, Title = "Narnia", TagIds = new List<int> { 20 } });

		var bookCache = _bookCache;
		var log = new SlotLog();
		log.OnFirstInit = () =>
			bookCache.AddOrUpdate(2, new MnTaggedBook { Id = 2, Title = "Narnia", TagIds = new List<int> { 20, 10 } });
		var container = new RecordingContainer(log);

		Resolver().ExecuteReverseMany(ref container, new[] { 10, 20 });

		Assert.Multiple(() => {
			Assert.That(log.Capacity[10], Is.EqualTo(1), "tag 10 was sized when only book 1 carried it");
			Assert.That(log.Adds[10], Is.EqualTo(2), "…and was then handed book 2 as well");
			Assert.That(log.Capacity[20], Is.EqualTo(2), "tag 20 was sized after, so its slot is right");
			Assert.That(log.Adds[20], Is.EqualTo(2));
		});
	}

	// The same defect through real per-left QueryResults over one shared buffer: the surplus row is
	// dropped, and the query does not fail.
	[Test]
	[NonParallelizable]
	public void JoinManyCollection_SurplusRowIsDroppedNotThrown() {
		QueryResultsDiagnostics.ResetDroppedRows();
		QueryResultsDiagnostics.ThrowOnSlotOverflowInDebug = false;
		try {
			_tagCache.AddOrUpdate(10, new MnTag { Id = 10, Name = "fantasy" });
			_tagCache.AddOrUpdate(20, new MnTag { Id = 20, Name = "classic" });
			_bookCache.AddOrUpdate(1, new MnTaggedBook { Id = 1, Title = "Hobbit", TagIds = new List<int> { 10, 20 } });
			_bookCache.AddOrUpdate(2, new MnTaggedBook { Id = 2, Title = "Narnia", TagIds = new List<int> { 20 } });

			var bookCache = _bookCache;
			var log = new SlotLog();
			log.OnFirstInit = () =>
				bookCache.AddOrUpdate(2, new MnTaggedBook { Id = 2, Title = "Narnia", TagIds = new List<int> { 20, 10 } });
			var container = new SlotSizingContainer(log, new SlotState(10, 20));

			Assert.That(() => Resolver().ExecuteReverseMany(ref container, new[] { 10, 20 }), Throws.Nothing);
			Assert.That(QueryResultsDiagnostics.DroppedRows, Is.EqualTo(1),
				"tag 10's slot had room for 1 and the fan-out delivered 2");
		} finally {
			QueryResultsDiagnostics.ThrowOnSlotOverflowInDebug = true;
		}
	}

	private IJoinManyResolver<int, MnTag, MnTaggedBook> Resolver()
		=> new JoinManyCollectionResolver<int, MnTag, InMemoryDataCache<int, MnTaggedBook>, int, MnTaggedBook, MnTaggedBook,
			NoFilter<CacheQueryBuilderCombined<NonExecutableQuery<InMemoryDataCache<int, MnTaggedBook>>, PairedCacheQueryBuilderCoreCombined<LeftKeySetView<int>, int, MnTaggedBook>, int, MnTaggedBook, Resolvers<BaseResolver<int, MnTaggedBook>>, MnTaggedBook>>>(
			_index.Forward, _index.Reverse, _bookCache, default);

	private sealed class SlotLog {
		public readonly Dictionary<int, int> Capacity = new();
		public readonly Dictionary<int, int> Adds = new();
		public Action? OnFirstInit;
		private bool _fired;

		public void FireOnce() {
			if (_fired)
				return;
			_fired = true;
			OnFirstInit?.Invoke();
		}
	}

	// Records what Init reserved and what Add delivered per left, without a backing buffer.
	// Init assigns, like the real QueryResults.SetPendingCapacity.
	private readonly struct RecordingContainer : IJoinedKeyedResultContainer<int, MnTaggedBook> {
		private readonly SlotLog _log;

		public RecordingContainer(SlotLog log) => _log = log;

		public void Init(int key, int maxCount) {
			_log.Capacity[key] = maxCount;
			_log.FireOnce();
		}

		public void Seal(int key, int actualCount) {
		}

		public int Add(int foreignKey, MnTaggedBook result) {
			var index = _log.Adds.GetValueOrDefault(foreignKey);
			_log.Adds[foreignKey] = index + 1;
			return index;
		}

		public int TotalCount {
			get {
				var total = 0;
				foreach (var capacity in _log.Capacity.Values)
					total += capacity;
				return total;
			}
		}

		public void PrepareSharedBuffer() {
		}

		public MnTaggedBook[]? GetSharedBuffer() => null;
	}

	private sealed class SlotState {
		public readonly Dictionary<int, QueryResults<MnTaggedBook>> Slots = new();
		public MnTaggedBook[]? Shared;
		public int TotalCount;

		public SlotState(params int[] leftKeys) {
			foreach (var key in leftKeys)
				Slots[key] = default;
		}
	}

	private readonly struct SlotSizingContainer : IJoinedKeyedResultContainer<int, MnTaggedBook> {
		private readonly SlotLog _log;
		private readonly SlotState _state;

		public SlotSizingContainer(SlotLog log, SlotState state) {
			_log = log;
			_state = state;
		}

		public int TotalCount => _state.TotalCount;

		public void Init(int key, int maxCount) {
			var slot = _state.Slots.GetValueOrDefault(key);
			slot.SetPendingCapacity(maxCount);
			_state.Slots[key] = slot;
			_state.TotalCount += maxCount;
			_log.Capacity[key] = maxCount;
			_log.FireOnce();
		}

		public void Seal(int key, int actualCount) {
		}

		public void PrepareSharedBuffer() {
			_state.Shared = new MnTaggedBook[_state.TotalCount];
			var offset = 0;
			foreach (var key in _state.Slots.Keys.ToArray()) {
				var slot = _state.Slots[key];
				offset = slot.AssignSharedBuffer(_state.Shared, offset);
				_state.Slots[key] = slot;
			}
		}

		public MnTaggedBook[]? GetSharedBuffer() => null;

		public int Add(int foreignKey, MnTaggedBook result) {
			if (!_state.Slots.TryGetValue(foreignKey, out var slot))
				return 0;
			var index = slot.UnsafeAdd(result);
			_state.Slots[foreignKey] = slot;
			_log.Adds[foreignKey] = _log.Adds.GetValueOrDefault(foreignKey) + 1;
			return index;
		}
	}
}
