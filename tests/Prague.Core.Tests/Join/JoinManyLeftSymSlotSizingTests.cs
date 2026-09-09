namespace Prague.Core.Tests.Join;

using System.Collections.Generic;
using Prague.Core;
using Prague.Core.TypeSystem;
using NUnit.Framework;

/// <summary>
///
///   <see cref="JoinManyLeftSymResolver{TLeftKey,TLeftValue,TRightCache,TLookupKey,TRightIndexKey,TRightKey,TRightValue,TFilter,TSelector}"/>
///   sizes each left's slot from a live <c>rightsBucket.Count</c> but delivers rows by fanning every
///   right out over a borrowed live <c>LeftKeySetView</c> at Add time. The reservation and the
///   delivery therefore come from different reads: a right that lands after one left of a lookup
///   group has been sized is still delivered to that left, so its slot is handed more rows than it
///   reserved. Unlike the right-list resolver, recording the pairs is not enough to fix it — one
///   recorded pair fans out to N lefts with N read later.
///
///   The fan-out fixed that: the resolver records explicit (left, right) pairs during the walk and
///   sizes each left's slot from the pairs recorded for it, so reserved and delivered are equal by
///   construction and no row is ever dropped. These tests asserted the broken numbers until then.
/// </summary>
[TestFixture]
public class JoinManyLeftSymSlotSizingTests {
	private InMemoryDataCache<int, MlsAuthor> _authors = null!;
	private InMemoryDataCache<int, MlsBook> _books = null!;
	private CacheSymmetricKeyValueListIndex<int, MlsAuthor, string> _authorCountrySymIdx = null!;
	private CacheKeyValueListIndex<int, MlsBook, string> _bookCountryIdx = null!;

	[SetUp]
	public void SetUp() {
		_authors = new InMemoryDataCache<int, MlsAuthor>();
		_books = new InMemoryDataCache<int, MlsBook>();
		_authorCountrySymIdx = _authors.CacheSymmetricKeyValueListIndex<string>((_, v) => v.Country);
		_bookCountryIdx = _books.CacheKeyValueListIndex<string>((_, v) => v.Country);
	}

	// A right added between two lefts of one lookup group. Deterministic: the container's Init hook
	// stands in for the writer, landing the book after author 1's slot has been sized. Author 1 keeps
	// the slot its own walk recorded, so the late book cannot overfill it.
	[Test]
	public void ExecuteOuter_RightAddedBetweenTwoLeftsOfAGroup_DeliversExactlyWhatItReserved() {
		_authors.AddOrUpdate(1, new MlsAuthor { Id = 1, Country = "UK", Name = "Tolkien" });
		_authors.AddOrUpdate(2, new MlsAuthor { Id = 2, Country = "UK", Name = "Lewis" });
		_books.AddOrUpdate(101, new MlsBook { Id = 101, Country = "UK", Title = "Hobbit" });
		_books.AddOrUpdate(102, new MlsBook { Id = 102, Country = "UK", Title = "Narnia" });

		var books = _books;
		var log = new SlotLog();
		log.OnFirstInit = () => books.AddOrUpdate(103, new MlsBook { Id = 103, Country = "UK", Title = "Late" });
		var container = new RecordingContainer(log);

		Resolver().ExecuteReverseMany(ref container, new[] { 1, 2 });

		Assert.That(log.Adds, Is.EqualTo(log.Capacity),
			"every left must be delivered exactly the rows its own walk reserved for it");
	}

	// The same interleaving through real per-left QueryResults over one shared buffer: nothing
	// overflows, so the never-overflow floor never has to drop anything.
	[Test]
	[NonParallelizable]
	public void JoinMany_LeftSym_NothingIsDropped() {
		QueryResultsDiagnostics.ResetDroppedRows();
		QueryResultsDiagnostics.ThrowOnSlotOverflowInDebug = false;
		try {
			_authors.AddOrUpdate(1, new MlsAuthor { Id = 1, Country = "UK", Name = "Tolkien" });
			_authors.AddOrUpdate(2, new MlsAuthor { Id = 2, Country = "UK", Name = "Lewis" });
			_books.AddOrUpdate(101, new MlsBook { Id = 101, Country = "UK", Title = "Hobbit" });
			_books.AddOrUpdate(102, new MlsBook { Id = 102, Country = "UK", Title = "Narnia" });

			var books = _books;
			var log = new SlotLog();
			log.OnFirstInit = () => books.AddOrUpdate(103, new MlsBook { Id = 103, Country = "UK", Title = "Late" });
			var container = new SlotSizingContainer(log, new SlotState(1, 2));

			Assert.That(() => Resolver().ExecuteReverseMany(ref container, new[] { 1, 2 }), Throws.Nothing);
			Assert.That(QueryResultsDiagnostics.DroppedRows, Is.Zero,
				"a slot was handed more rows than it reserved");
		} finally {
			QueryResultsDiagnostics.ThrowOnSlotOverflowInDebug = true;
		}
	}

	private IJoinManyResolver<int, MlsAuthor, MlsBook> Resolver()
		=> new JoinManyLeftSymResolver<int, MlsAuthor, InMemoryDataCache<int, MlsBook>, string, string, int, MlsBook,
			NoFilter<CacheQueryBuilderCombined<NonExecutableQuery<InMemoryDataCache<int, MlsBook>>, PairedCacheQueryBuilderCoreCombined<int, int, MlsBook>, int, MlsBook, Resolvers<BaseResolver<int, MlsBook>>, MlsBook>>,
			IdentitySelector<string>>(_authorCountrySymIdx, _books, _bookCountryIdx, default, default);

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
	private readonly struct RecordingContainer : IJoinedKeyedResultContainer<int, MlsBook> {
		private readonly SlotLog _log;

		public RecordingContainer(SlotLog log) => _log = log;

		public void Init(int key, int maxCount) {
			_log.Capacity[key] = maxCount;
			_log.FireOnce();
		}

		public void Seal(int key, int actualCount) {
		}

		public int Add(int foreignKey, MlsBook result) {
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

		public MlsBook[]? GetSharedBuffer() => null;
	}

	// Same interleaving, but backed by real per-left QueryResults over one shared buffer, so Add goes
	// through UnsafeAdd and the drop is observable on QueryResultsDiagnostics.
	private sealed class SlotState {
		public readonly Dictionary<int, QueryResults<MlsBook>> Slots = new();
		public MlsBook[]? Shared;
		public int TotalCount;

		public SlotState(params int[] leftKeys) {
			foreach (var key in leftKeys)
				Slots[key] = default;
		}
	}

	private readonly struct SlotSizingContainer : IJoinedKeyedResultContainer<int, MlsBook> {
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
			_state.Shared = new MlsBook[_state.TotalCount];
			var offset = 0;
			foreach (var key in _state.Slots.Keys.ToArray()) {
				var slot = _state.Slots[key];
				offset = slot.AssignSharedBuffer(_state.Shared, offset);
				_state.Slots[key] = slot;
			}
		}

		public MlsBook[]? GetSharedBuffer() => null;

		public int Add(int foreignKey, MlsBook result) {
			if (!_state.Slots.TryGetValue(foreignKey, out var slot))
				return 0;
			var index = slot.UnsafeAdd(result);
			_state.Slots[foreignKey] = slot;
			_log.Adds[foreignKey] = _log.Adds.GetValueOrDefault(foreignKey) + 1;
			return index;
		}
	}
}
