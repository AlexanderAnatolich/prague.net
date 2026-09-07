namespace Prague.Core.Tests.Leaks;

using System.Collections.Generic;
using System.Linq;
using Prague.Core;
using Prague.Core.Tests.Infrastructure;
using Prague.Core.Tests.Join;
using NUnit.Framework;

// Pool balance of the rounds-based JoinMany resolvers (LeftSym and collection, both directions).
// Every scenario drives the multi-round shape — six lefts sharing sixty rights, or sixty owners
// sharing six tags — so round 0 rents its arrays, the extra-rounds array is rented and grown, and
// extra rounds rent as they grow. Each must come back to zero new outstanding arrays and zero
// double-returns on the happy path, the Count path, and when the user filter or its predicate
// throws on the first, a middle or the last round (rounds already handed to the paired core are
// disposed there, the rest by JoinManyRounds.Dispose). Caches are built once per fixture so only
// per-query rentals show up in the delta.
[TestFixture]
[NonParallelizable]
public class JoinManyRoundsLeakTests {
	private const int SharingLefts = 6;
	private const int SharedRights = 60;

	private InMemoryDataCache<int, MlsAuthor> _authors = null!;
	private InMemoryDataCache<int, MlsBook> _books = null!;
	private CacheSymmetricKeyValueListIndex<int, MlsAuthor, string> _authorCountrySymIdx = null!;
	private CacheKeyValueListIndex<int, MlsBook, string> _bookCountryIdx = null!;

	private InMemoryDataCache<int, MnTag> _tags = null!;
	private InMemoryDataCache<int, MnTaggedBook> _taggedBooks = null!;
	private CacheCollectionSymmetricKeyValueListIndex<int, MnTaggedBook, int> _tagIndex = null!;

	// Same shapes keyed by ProbeKey on the right, for throwing inside the bucket walk.
	private InMemoryDataCache<ProbeKey, PkBook> _probeBooks = null!;
	private CacheKeyValueListIndex<ProbeKey, PkBook, string> _probeBookCountryIdx = null!;
	private InMemoryDataCache<ProbeKey, PkTaggedBook> _probeTaggedBooks = null!;
	private CacheCollectionSymmetricKeyValueListIndex<ProbeKey, PkTaggedBook, int> _probeTagIndex = null!;

	[OneTimeSetUp]
	public void BuildCaches() {
		_authors = new InMemoryDataCache<int, MlsAuthor>();
		_books = new InMemoryDataCache<int, MlsBook>();
		_authorCountrySymIdx = _authors.CacheSymmetricKeyValueListIndex<string>((_, v) => v.Country);
		_bookCountryIdx = _books.CacheKeyValueListIndex<string>((_, v) => v.Country);
		for (var a = 1; a <= SharingLefts; a++) {
			_authors.AddOrUpdate(a, new MlsAuthor { Id = a, Country = "UK", Name = "Author" + a });
		}

		_authors.AddOrUpdate(7, new MlsAuthor { Id = 7, Country = "DE", Name = "Author7" });
		_authors.AddOrUpdate(8, new MlsAuthor { Id = 8, Country = "FR", Name = "Author8" });
		for (var b = 0; b < SharedRights; b++) {
			_books.AddOrUpdate(101 + b, new MlsBook { Id = 101 + b, Country = "UK", Title = "Book" + b });
		}

		_books.AddOrUpdate(201, new MlsBook { Id = 201, Country = "DE", Title = "De1" });

		_tags = new InMemoryDataCache<int, MnTag>();
		_taggedBooks = new InMemoryDataCache<int, MnTaggedBook>();
		_tagIndex = _taggedBooks.CacheCollectionSymmetricKeyValueListIndex<int>((_, b) => b.TagIds);
		for (var t = 1; t <= SharingLefts + 1; t++) {
			_tags.AddOrUpdate(t, new MnTag { Id = t, Name = "tag" + t });
		}

		var allTags = Enumerable.Range(1, SharingLefts).ToList();
		for (var b = 0; b < SharedRights; b++) {
			_taggedBooks.AddOrUpdate(1001 + b, new MnTaggedBook { Id = 1001 + b, Title = "Book" + b, TagIds = new List<int>(allTags) });
		}

		_probeBooks = new InMemoryDataCache<ProbeKey, PkBook>();
		_probeBookCountryIdx = _probeBooks.CacheKeyValueListIndex<string>((_, v) => v.Country);
		for (var b = 0; b < SharedRights; b++) {
			_probeBooks.AddOrUpdate(new ProbeKey(101 + b), new PkBook { Id = 101 + b, Country = "UK", Title = "Book" + b });
		}

		_probeBooks.AddOrUpdate(new ProbeKey(201), new PkBook { Id = 201, Country = "DE", Title = "De1" });

		_probeTaggedBooks = new InMemoryDataCache<ProbeKey, PkTaggedBook>();
		_probeTagIndex = _probeTaggedBooks.CacheCollectionSymmetricKeyValueListIndex<int>((_, b) => b.TagIds);
		for (var b = 0; b < SharedRights; b++) {
			_probeTaggedBooks.AddOrUpdate(new ProbeKey(1001 + b), new PkTaggedBook { Id = 1001 + b, Title = "Book" + b, TagIds = new List<int>(allTags) });
		}
	}

	// Filter that lets the first (throwOn - 1) rounds through and throws on round throwOn.
	private static Func<TBuilder, TBuilder> ThrowingOnRound<TBuilder>(int throwOn) {
		var calls = 0;
		return builder => ++calls == throwOn ? throw new InvalidOperationException($"hostile filter on round {throwOn}") : builder;
	}

	private static void ExpectHostile(Action query) {
		try {
			query();
			Assert.Fail("filter must throw");
		} catch (InvalidOperationException) {
		}
	}

	// ── LeftSym ──────────────────────────────────────────────────────────────

	[Test]
	public void JoinManyLeftSymPooled_MultiRound_HappyPath_Balanced() =>
		LeakAssert.Balanced(() => {
			using var results = _authors.Query().JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(8));
			Assert.That(results.Count(r => r.Right.Count == SharedRights), Is.EqualTo(SharingLefts));
		});

	[Test]
	public void InnerJoinManyLeftSymPooled_MultiRound_HappyPath_Balanced() =>
		LeakAssert.Balanced(() => {
			using var results = _authors.Query().InnerJoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(7));
		});

	[Test]
	public void InnerJoinManyLeftSym_MultiRound_Count_Balanced() =>
		LeakAssert.Balanced(() => {
			var counted = _authors.Query().InnerJoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).Count();
			Assert.That(counted, Is.EqualTo(7));
		});

	[Test]
	public void JoinManyLeftSymPooled_MultiRound_DoubleDispose_NoDoubleReturn() =>
		LeakAssert.Balanced(() => {
			var results = _authors.Query().JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).ExecutePooled();
			results.Dispose();
			results.Dispose();
		});

	[TestCase(1)]
	[TestCase(2)]
	[TestCase(SharingLefts)]
	public void JoinManyLeftSymPooled_FilterThrowsOnRound_DoesNotLeak(int round) =>
		LeakAssert.Balanced(() => ExpectHostile(() =>
			_authors.Query().JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx, ThrowingOnRound<CacheQueryBuilderCombined<
				Prague.Core.TypeSystem.NonExecutableQuery<InMemoryDataCache<int, MlsBook>>,
				PairedCacheQueryBuilderCoreCombined<int, int, MlsBook>,
				int, MlsBook, Resolvers<BaseResolver<int, MlsBook>>, MlsBook>>(round)).ExecutePooled()));

	[TestCase(1)]
	[TestCase(2)]
	[TestCase(SharingLefts)]
	public void InnerJoinManyLeftSymPooled_FilterThrowsOnRound_DoesNotLeak(int round) =>
		LeakAssert.Balanced(() => ExpectHostile(() =>
			_authors.Query().InnerJoinMany(_authorCountrySymIdx, _books, _bookCountryIdx, ThrowingOnRound<CacheQueryBuilderCombined<
				Prague.Core.TypeSystem.NonExecutableQuery<InMemoryDataCache<int, MlsBook>>,
				PairedCacheQueryBuilderCoreCombined<int, int, MlsBook>,
				int, MlsBook, Resolvers<BaseResolver<int, MlsBook>>, MlsBook>>(round)).ExecutePooled()));

	// The predicate throws inside the store walk of round 2 (pair 70 of 360): that round's set is
	// already owned by the paired core, rounds 3..6 are still owned by JoinManyRounds.
	[Test]
	public void JoinManyLeftSymPooled_PredicateThrowsMidRound_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			var seen = 0;
			ExpectHostile(() =>
				_authors.Query().JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx,
					q => q.Where(_ => ++seen == SharedRights + 10 ? throw new InvalidOperationException("hostile predicate") : true)).ExecutePooled());
			Assert.That(seen, Is.EqualTo(SharedRights + 10));
		});

	[Test]
	public void InnerJoinManyLeftSymPooled_PredicateThrowsMidRound_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			var seen = 0;
			ExpectHostile(() =>
				_authors.Query().InnerJoinMany(_authorCountrySymIdx, _books, _bookCountryIdx,
					q => q.Where(_ => ++seen == SharedRights + 10 ? throw new InvalidOperationException("hostile predicate") : true)).ExecutePooled());
		});

	// ── Collection, reverse (element → owners): six rounds of sixty ──────────

	[Test]
	public void JoinManyCollectionPooled_MultiRound_HappyPath_Balanced() =>
		LeakAssert.Balanced(() => {
			using var results = _tags.Query().JoinManyCollection(_taggedBooks, _tagIndex).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(SharingLefts + 1));
			Assert.That(results.Count(r => r.Right.Count == SharedRights), Is.EqualTo(SharingLefts));
		});

	[Test]
	public void InnerJoinManyCollectionPooled_MultiRound_HappyPath_Balanced() =>
		LeakAssert.Balanced(() => {
			using var results = _tags.Query().InnerJoinManyCollection(_taggedBooks, _tagIndex).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(SharingLefts));
		});

	[Test]
	public void InnerJoinManyCollection_MultiRound_Count_Balanced() =>
		LeakAssert.Balanced(() => {
			var counted = _tags.Query().InnerJoinManyCollection(_taggedBooks, _tagIndex).Count();
			Assert.That(counted, Is.EqualTo(SharingLefts));
		});

	[TestCase(1)]
	[TestCase(2)]
	[TestCase(SharingLefts)]
	public void JoinManyCollectionPooled_FilterThrowsOnRound_DoesNotLeak(int round) =>
		LeakAssert.Balanced(() => ExpectHostile(() =>
			_tags.Query().JoinManyCollection(_taggedBooks, _tagIndex, ThrowingOnRound<CacheQueryBuilderCombined<
				Prague.Core.TypeSystem.NonExecutableQuery<InMemoryDataCache<int, MnTaggedBook>>,
				PairedCacheQueryBuilderCoreCombined<int, int, MnTaggedBook>,
				int, MnTaggedBook, Resolvers<BaseResolver<int, MnTaggedBook>>, MnTaggedBook>>(round)).ExecutePooled()));

	[TestCase(1)]
	[TestCase(2)]
	[TestCase(SharingLefts)]
	public void InnerJoinManyCollectionPooled_FilterThrowsOnRound_DoesNotLeak(int round) =>
		LeakAssert.Balanced(() => ExpectHostile(() =>
			_tags.Query().InnerJoinManyCollection(_taggedBooks, _tagIndex, ThrowingOnRound<CacheQueryBuilderCombined<
				Prague.Core.TypeSystem.NonExecutableQuery<InMemoryDataCache<int, MnTaggedBook>>,
				PairedCacheQueryBuilderCoreCombined<int, int, MnTaggedBook>,
				int, MnTaggedBook, Resolvers<BaseResolver<int, MnTaggedBook>>, MnTaggedBook>>(round)).ExecutePooled()));

	[Test]
	public void JoinManyCollectionPooled_PredicateThrowsMidRound_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			var seen = 0;
			ExpectHostile(() =>
				_tags.Query().JoinManyCollection(_taggedBooks, _tagIndex,
					q => q.Where(_ => ++seen == SharedRights + 10 ? throw new InvalidOperationException("hostile predicate") : true)).ExecutePooled());
		});

	// ── Collection, forward (owner → referenced): sixty rounds of six ────────

	[Test]
	public void JoinManyCollectionForwardPooled_ManyRounds_HappyPath_Balanced() =>
		LeakAssert.Balanced(() => {
			using var results = _taggedBooks.Query().JoinManyCollectionForward(_tags, _tagIndex).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(SharedRights));
			Assert.That(results.All(r => r.Right.Count == SharingLefts), Is.True);
		});

	[Test]
	public void InnerJoinManyCollectionForwardPooled_ManyRounds_HappyPath_Balanced() =>
		LeakAssert.Balanced(() => {
			using var results = _taggedBooks.Query().InnerJoinManyCollectionForward(_tags, _tagIndex).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(SharedRights));
		});

	[TestCase(1)]
	[TestCase(20)]
	[TestCase(SharedRights)]
	public void JoinManyCollectionForwardPooled_FilterThrowsOnRound_DoesNotLeak(int round) =>
		LeakAssert.Balanced(() => ExpectHostile(() =>
			_taggedBooks.Query().JoinManyCollectionForward(_tags, _tagIndex, ThrowingOnRound<CacheQueryBuilderCombined<
				Prague.Core.TypeSystem.NonExecutableQuery<InMemoryDataCache<int, MnTag>>,
				PairedCacheQueryBuilderCoreCombined<int, int, MnTag>,
				int, MnTag, Resolvers<BaseResolver<int, MnTag>>, MnTag>>(round)).ExecutePooled()));

	[TestCase(1)]
	[TestCase(20)]
	[TestCase(SharedRights)]
	public void InnerJoinManyCollectionForwardPooled_FilterThrowsOnRound_DoesNotLeak(int round) =>
		LeakAssert.Balanced(() => ExpectHostile(() =>
			_taggedBooks.Query().InnerJoinManyCollectionForward(_tags, _tagIndex, ThrowingOnRound<CacheQueryBuilderCombined<
				Prague.Core.TypeSystem.NonExecutableQuery<InMemoryDataCache<int, MnTag>>,
				PairedCacheQueryBuilderCoreCombined<int, int, MnTag>,
				int, MnTag, Resolvers<BaseResolver<int, MnTag>>, MnTag>>(round)).ExecutePooled()));

	// ── Throw inside the bucket walk (ProbeKey right keys) ───────────────────
	//
	// The right key's GetHashCode throws while the rounds are still being filled — after round 0
	// rented its arrays, the extra-rounds array was rented and grown past its initial four slots and
	// the extra rounds rented their own — so no round has reached a paired core yet and the user
	// filter never runs: JoinManyRounds.Dispose alone must return everything.

	// LeftSym hashes one right per pair (the start-round hint makes every first probe succeed); the
	// sixth sharing left opens round 5 — growing the extra-rounds array — at pair 301.
	private const int LeftSymWalkThrowAt = 5 * SharedRights + 30;

	// The collection resolver has no hint: the j-th sharing left probes j + 1 rounds per pair, so
	// five lefts cost 60 x 15 = 900 hashes and the sixth opens round 5 on its first pair.
	private const int CollectionWalkThrowAt = 15 * SharedRights + 100;

	[Test]
	public void JoinManyLeftSymPooled_RightKeyHashThrowsMidWalk_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			var filterCalls = 0;
			ProbeKey.ThrowOnHashCall(LeftSymWalkThrowAt);
			try {
				ExpectHostile(() => _authors.Query().JoinMany(_authorCountrySymIdx, _probeBooks, _probeBookCountryIdx, q => {
					filterCalls++;
					return q;
				}).ExecutePooled());
			} finally {
				ProbeKey.Disarm();
			}

			Assert.That(filterCalls, Is.Zero, "the throw must land in the bucket walk, before any round runs");
		});

	[Test]
	public void InnerJoinManyLeftSymPooled_RightKeyHashThrowsMidWalk_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			var filterCalls = 0;
			ProbeKey.ThrowOnHashCall(LeftSymWalkThrowAt);
			try {
				ExpectHostile(() => _authors.Query().InnerJoinMany(_authorCountrySymIdx, _probeBooks, _probeBookCountryIdx, q => {
					filterCalls++;
					return q;
				}).ExecutePooled());
			} finally {
				ProbeKey.Disarm();
			}

			Assert.That(filterCalls, Is.Zero, "the throw must land in the bucket walk, before any round runs");
		});

	[Test]
	public void JoinManyCollectionPooled_RightKeyHashThrowsMidWalk_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			var filterCalls = 0;
			ProbeKey.ThrowOnHashCall(CollectionWalkThrowAt);
			try {
				ExpectHostile(() => _tags.Query().JoinManyCollection(_probeTaggedBooks, _probeTagIndex, q => {
					filterCalls++;
					return q;
				}).ExecutePooled());
			} finally {
				ProbeKey.Disarm();
			}

			Assert.That(filterCalls, Is.Zero, "the throw must land in the bucket walk, before any round runs");
		});

	[Test]
	public void InnerJoinManyCollectionPooled_RightKeyHashThrowsMidWalk_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			var filterCalls = 0;
			ProbeKey.ThrowOnHashCall(CollectionWalkThrowAt);
			try {
				ExpectHostile(() => _tags.Query().InnerJoinManyCollection(_probeTaggedBooks, _probeTagIndex, q => {
					filterCalls++;
					return q;
				}).ExecutePooled());
			} finally {
				ProbeKey.Disarm();
			}

			Assert.That(filterCalls, Is.Zero, "the throw must land in the bucket walk, before any round runs");
		});
}
