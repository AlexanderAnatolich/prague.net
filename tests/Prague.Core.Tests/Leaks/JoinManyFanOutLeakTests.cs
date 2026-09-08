namespace Prague.Core.Tests.Leaks;

using System.Collections.Generic;
using System.Linq;
using Prague.Core;
using Prague.Core.Tests.Infrastructure;
using Prague.Core.Tests.Join;
using NUnit.Framework;

// Pool balance of the fan-out JoinMany resolvers (LeftSym and collection, both directions).
// Every scenario drives the shared-right shape — six lefts sharing sixty rights, or sixty owners
// sharing six tags — so the pair set rents its arrays and the chain arrays grow. Each must come
// back to zero new outstanding arrays and zero double-returns on the happy path, the Count path,
// and when the user filter or its predicate throws (a set already handed to the paired core is
// disposed there, everything else by JoinManyFanOut.Dispose). Caches are built once per fixture
// so only per-query rentals show up in the delta.
[TestFixture]
[NonParallelizable]
public class JoinManyFanOutLeakTests {
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

	// Filter that throws when the resolver applies it (once per query).
	private static Func<TBuilder, TBuilder> ThrowingFilter<TBuilder>() =>
		_ => throw new InvalidOperationException("hostile filter");

	private static void ExpectHostile(Action query) {
		try {
			query();
			Assert.Fail("filter must throw");
		} catch (InvalidOperationException) {
		}
	}

	// ── LeftSym ──────────────────────────────────────────────────────────────

	[Test]
	public void JoinManyLeftSymPooled_HappyPath_Balanced() =>
		LeakAssert.Balanced(() => {
			using var results = _authors.Query().JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(8));
			Assert.That(results.Count(r => r.Right.Count == SharedRights), Is.EqualTo(SharingLefts));
		});

	[Test]
	public void InnerJoinManyLeftSymPooled_HappyPath_Balanced() =>
		LeakAssert.Balanced(() => {
			using var results = _authors.Query().InnerJoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(7));
		});

	[Test]
	public void InnerJoinManyLeftSym_Count_Balanced() =>
		LeakAssert.Balanced(() => {
			var counted = _authors.Query().InnerJoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).Count();
			Assert.That(counted, Is.EqualTo(7));
		});

	[Test]
	public void JoinManyLeftSymPooled_DoubleDispose_NoDoubleReturn() =>
		LeakAssert.Balanced(() => {
			var results = _authors.Query().JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).ExecutePooled();
			results.Dispose();
			results.Dispose();
		});

	[Test]
	public void JoinManyLeftSymPooled_FilterThrows_DoesNotLeak() =>
		LeakAssert.Balanced(() => ExpectHostile(() =>
			_authors.Query().JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx, ThrowingFilter<CacheQueryBuilderCombined<
				Prague.Core.TypeSystem.NonExecutableQuery<InMemoryDataCache<int, MlsBook>>,
				PairedCacheQueryBuilderCoreCombined<int, int, MlsBook>,
				int, MlsBook, Resolvers<BaseResolver<int, MlsBook>>, MlsBook>>()).ExecutePooled()));

	[Test]
	public void InnerJoinManyLeftSymPooled_FilterThrows_DoesNotLeak() =>
		LeakAssert.Balanced(() => ExpectHostile(() =>
			_authors.Query().InnerJoinMany(_authorCountrySymIdx, _books, _bookCountryIdx, ThrowingFilter<CacheQueryBuilderCombined<
				Prague.Core.TypeSystem.NonExecutableQuery<InMemoryDataCache<int, MlsBook>>,
				PairedCacheQueryBuilderCoreCombined<int, int, MlsBook>,
				int, MlsBook, Resolvers<BaseResolver<int, MlsBook>>, MlsBook>>()).ExecutePooled()));

	// The predicate throws inside the store walk (right 30 of 60): the pair set is already owned by
	// the paired core, the chain arrays still by JoinManyFanOut.
	[Test]
	public void JoinManyLeftSymPooled_PredicateThrowsMidWalk_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			var seen = 0;
			ExpectHostile(() =>
				_authors.Query().JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx,
					q => q.Where(_ => ++seen == SharedRights / 2 ? throw new InvalidOperationException("hostile predicate") : true)).ExecutePooled());
			Assert.That(seen, Is.EqualTo(SharedRights / 2));
		});

	[Test]
	public void InnerJoinManyLeftSymPooled_PredicateThrowsMidWalk_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			var seen = 0;
			ExpectHostile(() =>
				_authors.Query().InnerJoinMany(_authorCountrySymIdx, _books, _bookCountryIdx,
					q => q.Where(_ => ++seen == SharedRights / 2 ? throw new InvalidOperationException("hostile predicate") : true)).ExecutePooled());
		});

	// ── Collection, reverse (element → owners): sixty rights shared by six tags ──

	[Test]
	public void JoinManyCollectionPooled_HappyPath_Balanced() =>
		LeakAssert.Balanced(() => {
			using var results = _tags.Query().JoinManyCollection(_taggedBooks, _tagIndex).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(SharingLefts + 1));
			Assert.That(results.Count(r => r.Right.Count == SharedRights), Is.EqualTo(SharingLefts));
		});

	[Test]
	public void InnerJoinManyCollectionPooled_HappyPath_Balanced() =>
		LeakAssert.Balanced(() => {
			using var results = _tags.Query().InnerJoinManyCollection(_taggedBooks, _tagIndex).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(SharingLefts));
		});

	[Test]
	public void InnerJoinManyCollection_Count_Balanced() =>
		LeakAssert.Balanced(() => {
			var counted = _tags.Query().InnerJoinManyCollection(_taggedBooks, _tagIndex).Count();
			Assert.That(counted, Is.EqualTo(SharingLefts));
		});

	[Test]
	public void JoinManyCollectionPooled_FilterThrows_DoesNotLeak() =>
		LeakAssert.Balanced(() => ExpectHostile(() =>
			_tags.Query().JoinManyCollection(_taggedBooks, _tagIndex, ThrowingFilter<CacheQueryBuilderCombined<
				Prague.Core.TypeSystem.NonExecutableQuery<InMemoryDataCache<int, MnTaggedBook>>,
				PairedCacheQueryBuilderCoreCombined<int, int, MnTaggedBook>,
				int, MnTaggedBook, Resolvers<BaseResolver<int, MnTaggedBook>>, MnTaggedBook>>()).ExecutePooled()));

	[Test]
	public void InnerJoinManyCollectionPooled_FilterThrows_DoesNotLeak() =>
		LeakAssert.Balanced(() => ExpectHostile(() =>
			_tags.Query().InnerJoinManyCollection(_taggedBooks, _tagIndex, ThrowingFilter<CacheQueryBuilderCombined<
				Prague.Core.TypeSystem.NonExecutableQuery<InMemoryDataCache<int, MnTaggedBook>>,
				PairedCacheQueryBuilderCoreCombined<int, int, MnTaggedBook>,
				int, MnTaggedBook, Resolvers<BaseResolver<int, MnTaggedBook>>, MnTaggedBook>>()).ExecutePooled()));

	[Test]
	public void JoinManyCollectionPooled_PredicateThrowsMidWalk_DoesNotLeak() =>
		LeakAssert.Balanced(() => {
			var seen = 0;
			ExpectHostile(() =>
				_tags.Query().JoinManyCollection(_taggedBooks, _tagIndex,
					q => q.Where(_ => ++seen == SharedRights / 2 ? throw new InvalidOperationException("hostile predicate") : true)).ExecutePooled());
		});

	// ── Collection, forward (owner → referenced): six tags shared by sixty owners ──

	[Test]
	public void JoinManyCollectionForwardPooled_HappyPath_Balanced() =>
		LeakAssert.Balanced(() => {
			using var results = _taggedBooks.Query().JoinManyCollectionForward(_tags, _tagIndex).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(SharedRights));
			Assert.That(results.All(r => r.Right.Count == SharingLefts), Is.True);
		});

	[Test]
	public void InnerJoinManyCollectionForwardPooled_HappyPath_Balanced() =>
		LeakAssert.Balanced(() => {
			using var results = _taggedBooks.Query().InnerJoinManyCollectionForward(_tags, _tagIndex).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(SharedRights));
		});

	[Test]
	public void JoinManyCollectionForwardPooled_FilterThrows_DoesNotLeak() =>
		LeakAssert.Balanced(() => ExpectHostile(() =>
			_taggedBooks.Query().JoinManyCollectionForward(_tags, _tagIndex, ThrowingFilter<CacheQueryBuilderCombined<
				Prague.Core.TypeSystem.NonExecutableQuery<InMemoryDataCache<int, MnTag>>,
				PairedCacheQueryBuilderCoreCombined<int, int, MnTag>,
				int, MnTag, Resolvers<BaseResolver<int, MnTag>>, MnTag>>()).ExecutePooled()));

	[Test]
	public void InnerJoinManyCollectionForwardPooled_FilterThrows_DoesNotLeak() =>
		LeakAssert.Balanced(() => ExpectHostile(() =>
			_taggedBooks.Query().InnerJoinManyCollectionForward(_tags, _tagIndex, ThrowingFilter<CacheQueryBuilderCombined<
				Prague.Core.TypeSystem.NonExecutableQuery<InMemoryDataCache<int, MnTag>>,
				PairedCacheQueryBuilderCoreCombined<int, int, MnTag>,
				int, MnTag, Resolvers<BaseResolver<int, MnTag>>, MnTag>>()).ExecutePooled()));

	// ── Throw inside the bucket walk (ProbeKey right keys) ───────────────────
	//
	// The right key's GetHashCode throws while the pairs are still being recorded — after the pair set
	// rented its arrays and the chain arrays grew past their initial capacity — so nothing has reached
	// a paired core yet and the user filter never runs: JoinManyFanOut.Dispose alone must return
	// everything.

	// The walk hashes one right per recorded pair; the sixth sharing left starts at pair 301, well
	// after the set and the chains left their initial capacity.
	private const int LeftSymWalkThrowAt = 5 * SharedRights + 30;

	// The collection walk hashes one right per recorded pair as well: five lefts cost 300 hashes
	// and the sixth starts at pair 301, like LeftSym.
	private const int CollectionWalkThrowAt = 5 * SharedRights + 30;

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

			Assert.That(filterCalls, Is.Zero, "the throw must land in the bucket walk, before the paired execute runs");
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

			Assert.That(filterCalls, Is.Zero, "the throw must land in the bucket walk, before the paired execute runs");
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

			Assert.That(filterCalls, Is.Zero, "the throw must land in the bucket walk, before the paired execute runs");
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

			Assert.That(filterCalls, Is.Zero, "the throw must land in the bucket walk, before the paired execute runs");
		});
}
