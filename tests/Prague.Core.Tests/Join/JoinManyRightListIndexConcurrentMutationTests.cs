namespace Prague.Core.Tests.Join;

using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Prague.Core;
using Prague.Core.TypeSystem;
using NUnit.Framework;

/// <summary>
///   The right-side list index is mutated by a live writer while a JoinMany query walks it.
///   Each left's result slot is a fixed partition of one shared buffer, so the number of rights
///   the resolver reserves for a left must bound the number it later adds — even when the bucket
///   changes between the two reads. A violation surfaces as
///   <see cref="IndexOutOfRangeException"/> ("UnsafeAdd overflow") from the real container.
/// </summary>
[TestFixture]
public class JoinManyRightListIndexConcurrentMutationTests {
	private static TimeSpan Duration => TimeSpan.FromSeconds(2);

	private InMemoryDataCache<int, MnAuthor> _authorCache = null!;
	private InMemoryDataCache<int, MnBook> _bookCache = null!;
	private CacheKeyValueListIndex<int, MnBook, int> _authorIdIndex = null!;

	[SetUp]
	public void SetUp() {
		_authorCache = new InMemoryDataCache<int, MnAuthor>();
		_bookCache = new InMemoryDataCache<int, MnBook>();
		_authorIdIndex = _bookCache.CacheKeyValueListIndex<int>((_, v) => v.AuthorId);
	}

	// Deterministic interleaving: the container's Init hook stands in for a writer that lands a
	// new right after the resolver has sized the slot but before it has walked the bucket.
	[Test]
	public void ExecuteReverse_RightAddedAfterSlotSized_AddsNeverExceedReservedCapacity() {
		_authorCache.AddOrUpdate(1, new MnAuthor { Id = 1, Name = "Tolkien" });
		for (var i = 0; i < 11; i++)
			_bookCache.AddOrUpdate(100 + i, new MnBook { Id = 100 + i, AuthorId = 1, Title = "Book" + i });

		var resolver = new JoinManyRightListIndexResolver<int, MnAuthor, InMemoryDataCache<int, MnBook>, int, int, MnBook,
			NoFilter<CacheQueryBuilderCombined<NonExecutableQuery<InMemoryDataCache<int, MnBook>>, PairedCacheQueryBuilderCoreCombined<int, int, MnBook>, int, MnBook, Resolvers<BaseResolver<int, MnBook>>, MnBook>>,
			IdentitySelector<int>>(_bookCache, _authorIdIndex, default, default);

		var bookCache = _bookCache;
		var log = new SlotLog {
			OnInit = authorId => bookCache.AddOrUpdate(999, new MnBook { Id = 999, AuthorId = authorId, Title = "Late" })
		};
		var container = new RecordingContainer(log);

		((IJoinManyResolver<int, MnAuthor, MnBook>)resolver).ExecuteReverseMany(ref container, new[] { 1 });

		Assert.That(log.Adds[1], Is.LessThanOrEqualTo(log.Capacity[1]),
			"slot for author 1 received more rights than the resolver reserved for it");
	}

	// Real interleaving: a single writer churns every author's bucket while outer and inner
	// JoinMany queries run back to back. Bounded to the same wall-clock budget as
	// ConcurrentReclamationStressTests.
	[Test]
	public void JoinMany_LiveRightWrites_NeverOverflowSlot() {
		const int authors = 2;
		const int booksPerAuthor = 64;
		var next = new int[authors + 1];
		for (var a = 1; a <= authors; a++) {
			_authorCache.AddOrUpdate(a, new MnAuthor { Id = a, Name = "Author" + a });
			for (var b = 0; b < booksPerAuthor; b++) {
				var id = BookId(a, next[a]++);
				_bookCache.AddOrUpdate(id, new MnBook { Id = id, AuthorId = a, Title = "Book" });
			}
		}

		var stop = false;
		var rounds = 0;
		// Every round grows each bucket by one fresh id and then trims its oldest: a Remove alone can
		// only under-fill a slot, so the Add is what the query races against.
		var writer = new Thread(() => {
			while (!Volatile.Read(ref stop)) {
				for (var a = 1; a <= authors; a++) {
					var added = BookId(a, next[a]++);
					_bookCache.AddOrUpdate(added, new MnBook { Id = added, AuthorId = a, Title = "Book" });
					_bookCache.Remove(BookId(a, next[a] - 1 - booksPerAuthor));
				}

				rounds++;
			}
		});

		Exception? failure = null;
		var queries = 0;
		writer.Start();
		try {
			var sw = Stopwatch.StartNew();
			while (sw.Elapsed < Duration) {
				try {
					if ((queries & 1) == 0) {
						using var results = _authorCache.Query().JoinMany(_bookCache, _authorIdIndex).ExecutePooled();
					} else {
						using var results = _authorCache.Query().InnerJoinMany(_bookCache, _authorIdIndex).ExecutePooled();
					}

					queries++;
				} catch (IndexOutOfRangeException e) {
					failure = e;
					break;
				}
			}
		} finally {
			Volatile.Write(ref stop, true);
			writer.Join();
		}

		Assert.That(failure, Is.Null, $"slot overflow after {queries} queries / {rounds} writer rounds: {failure}");
	}

	private static int BookId(int authorId, int ordinal) => authorId * 1_000_000 + ordinal;

	private sealed class SlotLog {
		public readonly Dictionary<int, int> Capacity = new();
		public readonly Dictionary<int, int> Adds = new();
		public Action<int>? OnInit;
	}

	// Mirrors the resolver's own keyed container contract without a backing buffer: records what
	// Init reserved and what Add delivered per left.
	private readonly struct RecordingContainer : IJoinedKeyedResultContainer<int, MnBook> {
		private readonly SlotLog _log;

		public RecordingContainer(SlotLog log) => _log = log;

		public void Init(int key, int maxCount) {
			_log.Capacity[key] = _log.Capacity.GetValueOrDefault(key) + maxCount;
			_log.OnInit?.Invoke(key);
		}

		public void Seal(int key, int actualCount) {
		}

		public int Add(int foreignKey, MnBook result) {
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

		public MnBook[]? GetSharedBuffer() => null;
	}
}
