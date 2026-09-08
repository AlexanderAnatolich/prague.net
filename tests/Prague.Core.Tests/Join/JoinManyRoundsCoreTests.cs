namespace Prague.Core.Tests.Join;

using System.Collections.Generic;
using System.Linq;
using Prague.Core;
using Prague.Core.Collections;
using Prague.Core.Tests.Infrastructure;
using NUnit.Framework;

// ── JoinManyRounds engine ────────────────────────────────────────────────────
// Direct tests of the first-fit rounds container the LeftSym and collection JoinMany resolvers
// build on: a pair goes to the earliest round (at or after the start hint) whose set does not yet
// hold its right key, extra rounds are pool-rented on the first collision, and Dispose returns
// everything the resolver did not hand off.

[TestFixture]
public class JoinManyRoundsTests {
	private const int RentedSetSize = 60; // > ValueSet inline capacity (47): the round's arrays come from the pool

	[Test]
	public void Add_DistinctRightKeys_AllLandInRoundZero() {
		var rounds = new JoinManyRounds<int, int>(8);
		try {
			for (var right = 100; right < 108; right++) {
				Assert.That(rounds.Add(new JoinedKeyPair<int, int>(1, right)), Is.Zero);
			}

			Assert.That(rounds.Count, Is.EqualTo(1));
			Assert.That(rounds[0].Count, Is.EqualTo(8));
		} finally {
			rounds.Dispose();
		}
	}

	[Test]
	public void Add_RepeatedRightKey_FirstFitsIntoSuccessiveRounds() {
		var rounds = new JoinManyRounds<int, int>(4);
		try {
			Assert.That(rounds.Add(new JoinedKeyPair<int, int>(1, 100)), Is.EqualTo(0));
			Assert.That(rounds.Add(new JoinedKeyPair<int, int>(2, 100)), Is.EqualTo(1));
			Assert.That(rounds.Add(new JoinedKeyPair<int, int>(3, 100)), Is.EqualTo(2));
			// A fresh right key still goes to round 0 whatever rounds exist.
			Assert.That(rounds.Add(new JoinedKeyPair<int, int>(3, 200)), Is.EqualTo(0));

			Assert.That(rounds.Count, Is.EqualTo(3));
			Assert.That(rounds[0].Count, Is.EqualTo(2));
			Assert.That(rounds[1].Count, Is.EqualTo(1));
			Assert.That(rounds[2].Count, Is.EqualTo(1));
		} finally {
			rounds.Dispose();
		}
	}

	[Test]
	public void Add_WithStartRound_SkipsEarlierRoundsEvenWhenTheyLackTheKey() {
		var rounds = new JoinManyRounds<int, int>(4);
		try {
			Assert.That(rounds.Add(new JoinedKeyPair<int, int>(1, 100), 0), Is.EqualTo(0));
			Assert.That(rounds.Add(new JoinedKeyPair<int, int>(2, 200), 1), Is.EqualTo(1), "the hint is honoured, not just the key test");
			Assert.That(rounds.Add(new JoinedKeyPair<int, int>(3, 200), 1), Is.EqualTo(2), "first-fit continues past the hint on a collision");

			Assert.That(rounds.Count, Is.EqualTo(3));
			Assert.That(rounds[0].Count, Is.EqualTo(1));
			Assert.That(rounds[1].Count, Is.EqualTo(1));
			Assert.That(rounds[2].Count, Is.EqualTo(1));
		} finally {
			rounds.Dispose();
		}
	}

	[Test]
	public void Add_StartRoundBeyondCount_ClampsToOneNewRound() {
		var rounds = new JoinManyRounds<int, int>(4);
		try {
			Assert.That(rounds.Add(new JoinedKeyPair<int, int>(1, 100), 7), Is.EqualTo(1));
			Assert.That(rounds.Count, Is.EqualTo(2));
			Assert.That(rounds[0].Count, Is.Zero);
			Assert.That(rounds[1].Count, Is.EqualTo(1));
		} finally {
			rounds.Dispose();
		}
	}

	// A right shared by m lefts needs m rounds. The unhinted Add must find the first free round with a
	// binary search over the rounds and hash the pair once for all of its probes: 64 pairs cost 64
	// hashes, not the 64 * 65 / 2 = 2080 a linear first-fit restarting at round 0 pays.
	[Test]
	public void Add_SameRightKeyManyTimes_HashesOncePerPair_AndLandsInConsecutiveRounds() {
		var rounds = new JoinManyRounds<int, ProbeKey>(4);
		var hashes = 0;
		ProbeKey.Arm(_ => hashes++);
		try {
			for (var left = 1; left <= 64; left++) {
				Assert.That(rounds.Add(new JoinedKeyPair<int, ProbeKey>(left, new ProbeKey(7))), Is.EqualTo(left - 1));
			}

			Assert.That(rounds.Count, Is.EqualTo(64));
			Assert.That(hashes, Is.EqualTo(64), "one hash per pair, however many rounds already hold the key");
		} finally {
			ProbeKey.Disarm();
			rounds.Dispose();
		}
	}

	// Hinted adds may leave a key in a later round without holding it in every earlier one. An unhinted
	// Add on such an instance still lands in a round free of the key and never adds a duplicate.
	[Test]
	public void Add_AfterHintedAddsBrokeThePrefix_StillLandsInARoundWithoutTheKey() {
		var rounds = new JoinManyRounds<int, int>(4);
		try {
			Assert.That(rounds.Add(new JoinedKeyPair<int, int>(1, 100)), Is.EqualTo(0));
			Assert.That(rounds.Add(new JoinedKeyPair<int, int>(1, 200), 1), Is.EqualTo(1), "hinted past round 0: key 200 sits in round 1 only");

			var landed = rounds.Add(new JoinedKeyPair<int, int>(2, 200));

			Assert.That(landed, Is.Not.EqualTo(1));
			Assert.That(rounds[landed].Contains(new JoinedKeyPair<int, int>(2, 200)), Is.True);
			var holders = 0;
			for (var i = 0; i < rounds.Count; i++) {
				if (rounds[i].Contains(new JoinedKeyPair<int, int>(0, 200))) {
					holders++;
				}
			}

			Assert.That(holders, Is.EqualTo(2), "each round holds the key at most once");
		} finally {
			rounds.Dispose();
		}
	}

	// Ten lefts on one right key: the extra-rounds array is rented on the first collision and grows
	// by rent-copy-return; every set must be reachable afterwards and Dispose must return it all.
	[Test]
	public void Add_ManyCollisions_GrowsExtraRoundsAndDisposeReturnsEverything() =>
		LeakAssert.Balanced(() => {
			var rounds = new JoinManyRounds<int, int>(4);
			try {
				for (var left = 1; left <= 40; left++) {
					Assert.That(rounds.Add(new JoinedKeyPair<int, int>(left, 100)), Is.EqualTo(left - 1));
				}

				Assert.That(rounds.Count, Is.EqualTo(40));
				for (var i = 0; i < rounds.Count; i++) {
					Assert.That(rounds[i].Count, Is.EqualTo(1), $"round {i}");
				}
			} finally {
				rounds.Dispose();
			}
		});

	// Large rounds rent their own arrays (round 0 up front, extra rounds as they grow).
	[Test]
	public void Add_LargeRounds_DisposeReturnsRentedSetArrays() =>
		LeakAssert.Balanced(() => {
			var rounds = new JoinManyRounds<int, int>(RentedSetSize);
			try {
				for (var left = 1; left <= 3; left++) {
					for (var right = 0; right < RentedSetSize; right++) {
						rounds.Add(new JoinedKeyPair<int, int>(left, right), left - 1);
					}
				}

				Assert.That(rounds.Count, Is.EqualTo(3));
				Assert.That(rounds[2].Count, Is.EqualTo(RentedSetSize));
			} finally {
				rounds.Dispose();
			}
		});

	// The resolver's hand-off discipline: a round moved into a paired core is overwritten with
	// default and disposed by the core; JoinManyRounds.Dispose must skip it (a second return would
	// register as a pool violation) while still disposing the rounds it kept.
	[Test]
	public void Dispose_SkipsHandedOffRound_DisposesTheRest() =>
		LeakAssert.Balanced(() => {
			var rounds = new JoinManyRounds<int, int>(RentedSetSize);
			try {
				for (var left = 1; left <= 3; left++) {
					for (var right = 0; right < RentedSetSize; right++) {
						rounds.Add(new JoinedKeyPair<int, int>(left, right), left - 1);
					}
				}

				var handedOff = rounds[1];
				rounds[1] = default;
				Assert.That(rounds[1].IsInitlized, Is.False);
				handedOff.Dispose();
			} finally {
				rounds.Dispose();
			}
		});

	// The right key's GetHashCode throws inside Add once every rent site is live: round 0 rented its
	// arrays, the extra-rounds array was rented and grown past its initial four slots. Dispose must
	// still return every array exactly once.
	[Test]
	public void Add_RightKeyHashThrows_AfterExtraRoundsGrew_DisposeReturnsEverything() =>
		LeakAssert.Balanced(() => {
			var rounds = new JoinManyRounds<int, ProbeKey>(RentedSetSize);
			try {
				for (var right = 0; right < RentedSetSize; right++) {
					rounds.Add(new JoinedKeyPair<int, ProbeKey>(1, new ProbeKey(right)));
				}

				for (var left = 2; left <= 7; left++) {
					Assert.That(rounds.Add(new JoinedKeyPair<int, ProbeKey>(left, new ProbeKey(0))), Is.EqualTo(left - 1));
				}

				Assert.That(rounds.Count, Is.EqualTo(7), "rounds 1..6 live in a grown extra-rounds array");

				ProbeKey.ThrowOnHashCall(1);
				try {
					rounds.Add(new JoinedKeyPair<int, ProbeKey>(8, new ProbeKey(0)));
					Assert.Fail("the right key hash must throw");
				} catch (InvalidOperationException) {
				}

				Assert.That(rounds.Count, Is.EqualTo(7), "a failed Add leaves the rounds as they were");
			} finally {
				ProbeKey.Disarm();
				rounds.Dispose();
			}
		});
}

// ── Multi-round joins through the public API ────────────────────────────────
// m lefts sharing n rights need m rounds; here m = 6 (past the initial extra-rounds array) and
// n = 60 (past the inline ValueSet capacity) for the LeftSym and reverse-collection shapes, and
// 60 owners sharing 6 tags (60 rounds of 6 pairs) for the forward-collection shape. Every left must
// receive every right, outer and inner, pooled and allocating.

[TestFixture]
public class JoinManyRoundsCoreTests {
	private const int SharingLefts = 6;
	private const int SharedRights = 60;

	private InMemoryDataCache<int, MlsAuthor> _authors = null!;
	private InMemoryDataCache<int, MlsBook> _books = null!;
	private CacheSymmetricKeyValueListIndex<int, MlsAuthor, string> _authorCountrySymIdx = null!;
	private CacheKeyValueListIndex<int, MlsBook, string> _bookCountryIdx = null!;

	private InMemoryDataCache<int, MnTag> _tags = null!;
	private InMemoryDataCache<int, MnTaggedBook> _taggedBooks = null!;
	private CacheCollectionSymmetricKeyValueListIndex<int, MnTaggedBook, int> _tagIndex = null!;

	[SetUp]
	public void SetUp() {
		_authors = new InMemoryDataCache<int, MlsAuthor>();
		_books = new InMemoryDataCache<int, MlsBook>();
		_authorCountrySymIdx = _authors.CacheSymmetricKeyValueListIndex<string>((_, v) => v.Country);
		_bookCountryIdx = _books.CacheKeyValueListIndex<string>((_, v) => v.Country);

		// Authors 1..6 share the UK bucket of 60 books; author 7 (DE) has two; author 8 (FR) none.
		for (var a = 1; a <= SharingLefts; a++) {
			_authors.AddOrUpdate(a, new MlsAuthor { Id = a, Country = "UK", Name = "Author" + a });
		}

		_authors.AddOrUpdate(7, new MlsAuthor { Id = 7, Country = "DE", Name = "Author7" });
		_authors.AddOrUpdate(8, new MlsAuthor { Id = 8, Country = "FR", Name = "Author8" });
		for (var b = 0; b < SharedRights; b++) {
			_books.AddOrUpdate(101 + b, new MlsBook { Id = 101 + b, Country = "UK", Title = "Book" + b });
		}

		_books.AddOrUpdate(201, new MlsBook { Id = 201, Country = "DE", Title = "De1" });
		_books.AddOrUpdate(202, new MlsBook { Id = 202, Country = "DE", Title = "De2" });

		_tags = new InMemoryDataCache<int, MnTag>();
		_taggedBooks = new InMemoryDataCache<int, MnTaggedBook>();
		_tagIndex = _taggedBooks.CacheCollectionSymmetricKeyValueListIndex<int>((_, b) => b.TagIds);

		// Tags 1..6; 60 books carrying every tag; tag 7 unused.
		for (var t = 1; t <= SharingLefts + 1; t++) {
			_tags.AddOrUpdate(t, new MnTag { Id = t, Name = "tag" + t });
		}

		var allTags = Enumerable.Range(1, SharingLefts).ToList();
		for (var b = 0; b < SharedRights; b++) {
			_taggedBooks.AddOrUpdate(1001 + b, new MnTaggedBook { Id = 1001 + b, Title = "Book" + b, TagIds = new List<int>(allTags) });
		}
	}

	private static int[] UkBookIds => Enumerable.Range(101, SharedRights).ToArray();
	private static int[] AllTagIds => Enumerable.Range(1, SharingLefts).ToArray();
	private static int[] AllBookIds => Enumerable.Range(1001, SharedRights).ToArray();

	private static int[] Ids<TLeft, TRight>(JoinResult<TLeft, QueryResults<TRight>> row, Func<TRight, int> id) =>
		row.Right.Select(id).OrderBy(i => i).ToArray();

	// ── LeftSym ──────────────────────────────────────────────────────────────

	[Test]
	public void JoinMany_LeftSym_SixLeftsSharingSixtyRights_EachReceivesAll() {
		var results = _authors.Query().JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).Execute();

		Assert.That(results.Count, Is.EqualTo(8));
		var byId = results.ToDictionary(r => r.Left.Id);
		for (var a = 1; a <= SharingLefts; a++) {
			Assert.That(Ids(byId[a], b => b.Id), Is.EqualTo(UkBookIds), $"author {a}");
		}

		Assert.That(Ids(byId[7], b => b.Id), Is.EqualTo(new[] { 201, 202 }));
		Assert.That(byId[8].Right.Count, Is.Zero);
	}

	[Test]
	public void InnerJoinMany_LeftSym_SixLeftsSharingSixtyRights_EachReceivesAll() {
		var results = _authors.Query().InnerJoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).Execute();

		Assert.That(results.Select(r => r.Left.Id).OrderBy(i => i), Is.EqualTo(Enumerable.Range(1, 7)));
		var byId = results.ToDictionary(r => r.Left.Id);
		for (var a = 1; a <= SharingLefts; a++) {
			Assert.That(Ids(byId[a], b => b.Id), Is.EqualTo(UkBookIds), $"author {a}");
		}

		Assert.That(Ids(byId[7], b => b.Id), Is.EqualTo(new[] { 201, 202 }));
	}

	[Test]
	public void JoinMany_LeftSym_MultiRound_PooledEqualsExecute() {
		var expected = _authors.Query().JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).Execute()
			.ToDictionary(r => r.Left.Id, r => Ids(r, b => b.Id));
		using var pooled = _authors.Query().JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).ExecutePooled();

		var actual = pooled.ToDictionary(r => r.Left.Id, r => Ids(r, b => b.Id));
		Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
		foreach (var (id, rights) in expected) {
			Assert.That(actual[id], Is.EqualTo(rights), $"author {id}");
		}
	}

	[Test]
	public void InnerJoinMany_LeftSym_MultiRound_CountMatchesExecute() {
		var executed = _authors.Query().InnerJoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).Execute().Count;
		var counted = _authors.Query().InnerJoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).Count();
		Assert.That(counted, Is.EqualTo(executed));
	}

	// ── Collection, reverse (element → owners) ───────────────────────────────

	[Test]
	public void JoinManyCollection_OwnersSharedBySixElements_EachElementReceivesAll() {
		var results = _tags.Query().JoinManyCollection(_taggedBooks, _tagIndex).Execute();

		Assert.That(results.Count, Is.EqualTo(SharingLefts + 1));
		var byId = results.ToDictionary(r => r.Left.Id);
		for (var t = 1; t <= SharingLefts; t++) {
			Assert.That(Ids(byId[t], b => b.Id), Is.EqualTo(AllBookIds), $"tag {t}");
		}

		Assert.That(byId[SharingLefts + 1].Right.Count, Is.Zero);
	}

	[Test]
	public void InnerJoinManyCollection_OwnersSharedBySixElements_EachElementReceivesAll() {
		var results = _tags.Query().InnerJoinManyCollection(_taggedBooks, _tagIndex).Execute();

		Assert.That(results.Select(r => r.Left.Id).OrderBy(i => i), Is.EqualTo(AllTagIds));
		foreach (var row in results) {
			Assert.That(Ids(row, b => b.Id), Is.EqualTo(AllBookIds), $"tag {row.Left.Id}");
		}
	}

	[Test]
	public void JoinManyCollection_MultiRound_PooledEqualsExecute() {
		var expected = _tags.Query().JoinManyCollection(_taggedBooks, _tagIndex).Execute()
			.ToDictionary(r => r.Left.Id, r => Ids(r, b => b.Id));
		using var pooled = _tags.Query().JoinManyCollection(_taggedBooks, _tagIndex).ExecutePooled();

		var actual = pooled.ToDictionary(r => r.Left.Id, r => Ids(r, b => b.Id));
		Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
		foreach (var (id, rights) in expected) {
			Assert.That(actual[id], Is.EqualTo(rights), $"tag {id}");
		}
	}

	// ── Collection, forward (owner → referenced) — 60 rounds of 6 pairs ──────

	[Test]
	public void JoinManyCollectionForward_SixtyOwnersSharingSixTags_EachReceivesAll() {
		var results = _taggedBooks.Query().JoinManyCollectionForward(_tags, _tagIndex).Execute();

		Assert.That(results.Count, Is.EqualTo(SharedRights));
		foreach (var row in results) {
			Assert.That(Ids(row, t => t.Id), Is.EqualTo(AllTagIds), $"book {row.Left.Id}");
		}
	}

	[Test]
	public void InnerJoinManyCollectionForward_SixtyOwnersSharingSixTags_EachReceivesAll() {
		var results = _taggedBooks.Query().InnerJoinManyCollectionForward(_tags, _tagIndex).Execute();

		Assert.That(results.Count, Is.EqualTo(SharedRights));
		foreach (var row in results) {
			Assert.That(Ids(row, t => t.Id), Is.EqualTo(AllTagIds), $"book {row.Left.Id}");
		}
	}

	[Test]
	public void JoinManyCollectionForward_ManyRounds_PooledEqualsExecute() {
		var expected = _taggedBooks.Query().JoinManyCollectionForward(_tags, _tagIndex).Execute()
			.ToDictionary(r => r.Left.Id, r => Ids(r, t => t.Id));
		using var pooled = _taggedBooks.Query().JoinManyCollectionForward(_tags, _tagIndex).ExecutePooled();

		var actual = pooled.ToDictionary(r => r.Left.Id, r => Ids(r, t => t.Id));
		Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
		foreach (var (id, rights) in expected) {
			Assert.That(actual[id], Is.EqualTo(rights), $"book {id}");
		}
	}

	// ── The user filter runs once per round; its predicate sees every pair exactly once ──
	//
	// Three lefts sharing four rights: three rounds of four pairs. The filter lambda (the
	// builder-shaping callback) is invoked once per non-empty round; the Where predicate it installs
	// runs inside the store walk once per pair, so an always-true predicate yields exactly the
	// unfiltered result.

	private void ThreeAuthorsFourBooks() {
		_authors = new InMemoryDataCache<int, MlsAuthor>();
		_books = new InMemoryDataCache<int, MlsBook>();
		_authorCountrySymIdx = _authors.CacheSymmetricKeyValueListIndex<string>((_, v) => v.Country);
		_bookCountryIdx = _books.CacheKeyValueListIndex<string>((_, v) => v.Country);
		for (var a = 1; a <= 3; a++) {
			_authors.AddOrUpdate(a, new MlsAuthor { Id = a, Country = "UK", Name = "Author" + a });
		}

		for (var b = 101; b <= 104; b++) {
			_books.AddOrUpdate(b, new MlsBook { Id = b, Country = "UK", Title = "Book" + b });
		}
	}

	private void FourBooksWithThreeTags() {
		_tags = new InMemoryDataCache<int, MnTag>();
		_taggedBooks = new InMemoryDataCache<int, MnTaggedBook>();
		_tagIndex = _taggedBooks.CacheCollectionSymmetricKeyValueListIndex<int>((_, b) => b.TagIds);
		for (var t = 1; t <= 3; t++) {
			_tags.AddOrUpdate(t, new MnTag { Id = t, Name = "tag" + t });
		}

		for (var b = 1001; b <= 1004; b++) {
			_taggedBooks.AddOrUpdate(b, new MnTaggedBook { Id = b, Title = "Book" + b, TagIds = new List<int> { 1, 2, 3 } });
		}
	}

	[Test]
	public void JoinMany_LeftSym_FilterRunsOncePerRound_PredicateSeesEachPairOnce() {
		ThreeAuthorsFourBooks();
		var filterCalls = 0;
		var predicateCalls = 0;

		var unfiltered = _authors.Query().JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx).Execute()
			.ToDictionary(r => r.Left.Id, r => Ids(r, b => b.Id));
		var filtered = _authors.Query()
			.JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx, q => {
				filterCalls++;
				return q.Where(_ => {
					predicateCalls++;
					return true;
				});
			})
			.Execute()
			.ToDictionary(r => r.Left.Id, r => Ids(r, b => b.Id));

		Assert.That(filterCalls, Is.EqualTo(3), "one filter application per round");
		Assert.That(predicateCalls, Is.EqualTo(12), "the predicate sees every (left, right) pair exactly once");
		Assert.That(filtered, Is.EqualTo(unfiltered));
	}

	[Test]
	public void InnerJoinMany_LeftSym_FilterRunsOncePerRound_PredicateSeesEachPairOnce() {
		ThreeAuthorsFourBooks();
		var filterCalls = 0;
		var predicateCalls = 0;

		var results = _authors.Query()
			.InnerJoinMany(_authorCountrySymIdx, _books, _bookCountryIdx, q => {
				filterCalls++;
				return q.Where(_ => {
					predicateCalls++;
					return true;
				});
			})
			.Execute();

		Assert.That(filterCalls, Is.EqualTo(3));
		Assert.That(predicateCalls, Is.EqualTo(12));
		Assert.That(results.Count, Is.EqualTo(3));
		Assert.That(results.All(r => r.Right.Count == 4), Is.True);
	}

	[Test]
	public void JoinMany_LeftSym_FilterRejectingPairs_NarrowsEveryRoundAlike() {
		ThreeAuthorsFourBooks();

		var results = _authors.Query()
			.JoinMany(_authorCountrySymIdx, _books, _bookCountryIdx, q => q.Where(b => b.Id % 2 == 0))
			.Execute();

		Assert.That(results.Count, Is.EqualTo(3));
		foreach (var row in results) {
			Assert.That(Ids(row, b => b.Id), Is.EqualTo(new[] { 102, 104 }), $"author {row.Left.Id}");
		}
	}

	[Test]
	public void JoinManyCollection_FilterRunsOncePerRound_PredicateSeesEachPairOnce() {
		FourBooksWithThreeTags();
		var filterCalls = 0;
		var predicateCalls = 0;

		var results = _tags.Query()
			.JoinManyCollection(_taggedBooks, _tagIndex, q => {
				filterCalls++;
				return q.Where(_ => {
					predicateCalls++;
					return true;
				});
			})
			.Execute();

		Assert.That(filterCalls, Is.EqualTo(3), "three tags share four books: three rounds");
		Assert.That(predicateCalls, Is.EqualTo(12));
		Assert.That(results.Count, Is.EqualTo(3));
		Assert.That(results.All(r => r.Right.Count == 4), Is.True);
	}

	[Test]
	public void JoinManyCollectionForward_FilterRunsOncePerRound_PredicateSeesEachPairOnce() {
		FourBooksWithThreeTags();
		var filterCalls = 0;
		var predicateCalls = 0;

		var results = _taggedBooks.Query()
			.JoinManyCollectionForward(_tags, _tagIndex, q => {
				filterCalls++;
				return q.Where(_ => {
					predicateCalls++;
					return true;
				});
			})
			.Execute();

		Assert.That(filterCalls, Is.EqualTo(4), "four books share three tags: four rounds");
		Assert.That(predicateCalls, Is.EqualTo(12));
		Assert.That(results.Count, Is.EqualTo(4));
		Assert.That(results.All(r => r.Right.Count == 3), Is.True);
	}
}
