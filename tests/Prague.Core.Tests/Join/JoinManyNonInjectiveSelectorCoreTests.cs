namespace Prague.Core.Tests.Join;

using System.Collections.Generic;
using System.Linq;
using Prague.Core;
using NUnit.Framework;

// ── Non-injective key selectors ──────────────────────────────────────────────
// A JoinMany key selector that maps several lookup keys onto ONE right-index key makes distinct
// lefts share the same right bucket. Pair identity is the right key alone, so a single pair set
// cannot carry (L1, r) and (L2, r) — every JoinMany resolver chains such lefts behind one pair per
// right (the fan-out) and every left receives the full bucket.
//
//   LeftSym:   MlsAuthor.Country is a region-qualified code ("UK-A", "UK-B"); the selector keeps
//              the two-letter prefix, so both regions fold onto MlsBook.Country == "UK".
//   RightList: MnBook.AuthorId is read as a department id; the selector maps authors 1 and 2 to
//              department 10 and author 3 to department 20.

[TestFixture]
public class JoinManyNonInjectiveSelectorCoreTests {
	// ── LeftSym fixture ──────────────────────────────────────────────────────

	private InMemoryDataCache<int, MlsAuthor> _authors = null!;
	private InMemoryDataCache<int, MlsBook> _books = null!;
	private CacheSymmetricKeyValueListIndex<int, MlsAuthor, string> _authorRegionSymIdx = null!;
	private CacheKeyValueListIndex<int, MlsBook, string> _bookCountryIdx = null!;

	// ── RightList fixture ────────────────────────────────────────────────────

	private InMemoryDataCache<int, MnAuthor> _employees = null!;
	private InMemoryDataCache<int, MnBook> _tasks = null!;
	private CacheKeyValueListIndex<int, MnBook, int> _taskDeptIdx = null!;

	[SetUp]
	public void SetUp() {
		_authors = new InMemoryDataCache<int, MlsAuthor>();
		_books = new InMemoryDataCache<int, MlsBook>();
		_authorRegionSymIdx = _authors.CacheSymmetricKeyValueListIndex<string>((_, v) => v.Country);
		_bookCountryIdx = _books.CacheKeyValueListIndex<string>((_, v) => v.Country);

		_authors.AddOrUpdate(1, new MlsAuthor { Id = 1, Country = "UK-A", Name = "Tolkien" });
		_authors.AddOrUpdate(2, new MlsAuthor { Id = 2, Country = "UK-B", Name = "Lewis" });
		_authors.AddOrUpdate(3, new MlsAuthor { Id = 3, Country = "US-A", Name = "Asimov" });
		_authors.AddOrUpdate(4, new MlsAuthor { Id = 4, Country = "FR-A", Name = "OrphanFrench" });

		_books.AddOrUpdate(101, new MlsBook { Id = 101, Country = "UK", Title = "Hobbit" });
		_books.AddOrUpdate(102, new MlsBook { Id = 102, Country = "UK", Title = "Narnia" });
		_books.AddOrUpdate(201, new MlsBook { Id = 201, Country = "US", Title = "Foundation" });

		_employees = new InMemoryDataCache<int, MnAuthor>();
		_tasks = new InMemoryDataCache<int, MnBook>();
		_taskDeptIdx = _tasks.CacheKeyValueListIndex<int>((_, v) => v.AuthorId);

		_employees.AddOrUpdate(1, new MnAuthor { Id = 1, Name = "Ann" });
		_employees.AddOrUpdate(2, new MnAuthor { Id = 2, Name = "Bob" });
		_employees.AddOrUpdate(3, new MnAuthor { Id = 3, Name = "Cid" });

		_tasks.AddOrUpdate(101, new MnBook { Id = 101, AuthorId = 10, Title = "deploy" });
		_tasks.AddOrUpdate(102, new MnBook { Id = 102, AuthorId = 10, Title = "review" });
		_tasks.AddOrUpdate(201, new MnBook { Id = 201, AuthorId = 20, Title = "design" });
	}

	private static string CountryOf(string region) => region.Substring(0, 2);
	private static int DeptOf(int employeeKey) => employeeKey <= 2 ? 10 : 20;

	private static int[] RightIds(JoinResult<MlsAuthor, QueryResults<MlsBook>> row) => row.Right.Select(b => b.Id).OrderBy(i => i).ToArray();

	// ── LeftSym: the fan-out delivers the shared bucket to every folded group ─────

	[Test]
	public void JoinMany_LeftSym_NonInjectiveSelector_EveryLeftGetsItsRights() {
		var results = _authors.Query()
			.JoinMany(_authorRegionSymIdx, (string region) => CountryOf(region), _books, _bookCountryIdx)
			.Execute();

		Assert.That(results.Count, Is.EqualTo(4));
		var byId = results.ToDictionary(r => r.Left.Id);
		Assert.That(RightIds(byId[1]), Is.EqualTo(new[] { 101, 102 }), "author 1 (UK-A)");
		Assert.That(RightIds(byId[2]), Is.EqualTo(new[] { 101, 102 }), "author 2 (UK-B) lost its rights to right-key pair dedup");
		Assert.That(RightIds(byId[3]), Is.EqualTo(new[] { 201 }), "author 3 (US-A)");
		Assert.That(byId[4].Right.Count, Is.Zero, "author 4 (FR-A) has no books");
	}

	[Test]
	public void JoinMany_LeftSym_NonInjectiveSelector_WithFilter_EveryLeftGetsFilteredRights() {
		var results = _authors.Query()
			.JoinMany(_authorRegionSymIdx, (string region) => CountryOf(region), _books, _bookCountryIdx,
				q => q.Where(b => b.Title.StartsWith("Nar")))
			.Execute();

		var byId = results.ToDictionary(r => r.Left.Id);
		Assert.That(RightIds(byId[1]), Is.EqualTo(new[] { 102 }));
		Assert.That(RightIds(byId[2]), Is.EqualTo(new[] { 102 }), "the filter's predicate keeps Narnia for every left chained behind it");
		Assert.That(byId[3].Right.Count, Is.Zero);
	}

	[Test]
	public void InnerJoinMany_LeftSym_NonInjectiveSelector_EveryLeftGetsItsRights() {
		var results = _authors.Query()
			.InnerJoinMany(_authorRegionSymIdx, (string region) => CountryOf(region), _books, _bookCountryIdx)
			.Execute();

		Assert.That(results.Select(r => r.Left.Id).OrderBy(i => i), Is.EqualTo(new[] { 1, 2, 3 }));
		var byId = results.ToDictionary(r => r.Left.Id);
		Assert.That(RightIds(byId[1]), Is.EqualTo(new[] { 101, 102 }));
		Assert.That(RightIds(byId[2]), Is.EqualTo(new[] { 101, 102 }), "author 2 must survive the inner join with the full bucket");
		Assert.That(RightIds(byId[3]), Is.EqualTo(new[] { 201 }));
	}

	[Test]
	public void InnerJoinMany_LeftSym_NonInjectiveSelector_FilterDropsOnlyLeftsWithoutMatches() {
		var results = _authors.Query()
			.InnerJoinMany(_authorRegionSymIdx, (string region) => CountryOf(region), _books, _bookCountryIdx,
				q => q.Where(b => b.Title.StartsWith("Hob")))
			.Execute();

		Assert.That(results.Select(r => r.Left.Id).OrderBy(i => i), Is.EqualTo(new[] { 1, 2 }));
		Assert.That(results.All(r => r.Right.Single().Id == 101), Is.True);
	}

	[Test]
	public void JoinMany_LeftSym_NonInjectiveSelector_PooledEqualsExecute() {
		var expected = _authors.Query()
			.JoinMany(_authorRegionSymIdx, (string region) => CountryOf(region), _books, _bookCountryIdx)
			.Execute()
			.ToDictionary(r => r.Left.Id, RightIds);
		using var pooled = _authors.Query()
			.JoinMany(_authorRegionSymIdx, (string region) => CountryOf(region), _books, _bookCountryIdx)
			.ExecutePooled();

		var actual = pooled.ToDictionary(r => r.Left.Id, RightIds);
		Assert.That(actual.Keys, Is.EquivalentTo(expected.Keys));
		foreach (var (id, rights) in expected) {
			Assert.That(actual[id], Is.EqualTo(rights), $"author {id}");
		}
	}

	// ── RightList: a shared bucket reaches every left of the group ─────────
	//
	// JoinManyRightListIndexResolver records every left's bucket into the fan-out. With a non-injective
	// selector the second left of a group finds its right keys already paired with the first left; it
	// joins their chains, its slot is sized from the pairs it recorded and the single execute delivers
	// the bucket to it as well. Before the fan-out its slot was sized 0 and it silently received nothing.

	[Test]
	public void JoinMany_RightList_NonInjectiveSelector_SecondLeftOfGroupReceivesTheFullBucket() {
		var results = _employees.Query()
			.JoinMany(static k => DeptOf(k), _tasks, _taskDeptIdx)
			.Execute();

		Assert.That(results.Count, Is.EqualTo(3));
		var byId = results.ToDictionary(r => r.Left.Id);
		Assert.That(byId[1].Right.Select(t => t.Id).OrderBy(i => i), Is.EqualTo(new[] { 101, 102 }), "employee 1 (dept 10, walked first)");
		Assert.That(byId[2].Right.Select(t => t.Id).OrderBy(i => i), Is.EqualTo(new[] { 101, 102 }),
			"employee 2 (same dept 10, walked second) receives the shared bucket through the fan-out");
		Assert.That(byId[3].Right.Select(t => t.Id), Is.EqualTo(new[] { 201 }), "employee 3 (dept 20)");
	}

	// A collection-backed right index (one book in several tag buckets) reaches the same shape
	// through identity keys: buckets of DIFFERENT index keys overlap. The plain right-list JoinMany
	// delivers the shared right to every tag whose bucket holds it, exactly like JoinManyCollection
	// over the symmetric collection index does.
	[Test]
	public void JoinMany_RightList_OverlappingCollectionBuckets_SharedRightReachesEveryLeft() {
		var tags = new InMemoryDataCache<int, MnTag>();
		var books = new InMemoryDataCache<int, MnTaggedBook>();
		var tagListIdx = books.CacheCollectionKeyValueListIndex<int>((_, b) => b.TagIds);
		var tagSymIdx = books.CacheCollectionSymmetricKeyValueListIndex<int>((_, b) => b.TagIds);

		tags.AddOrUpdate(10, new MnTag { Id = 10, Name = "tag10" });
		tags.AddOrUpdate(20, new MnTag { Id = 20, Name = "tag20" });
		books.AddOrUpdate(1, new MnTaggedBook { Id = 1, Title = "Shared", TagIds = new List<int> { 10, 20 } });
		books.AddOrUpdate(2, new MnTaggedBook { Id = 2, Title = "Only10", TagIds = new List<int> { 10 } });

		var viaRightList = tags.Query().JoinMany(books, tagListIdx).Execute().ToDictionary(r => r.Left.Id);
		Assert.That(viaRightList[10].Right.Select(b => b.Id), Does.Contain(2), "book 2 is in tag 10's bucket only and always reaches it");
		Assert.That(viaRightList[10].Right.Select(b => b.Id).OrderBy(i => i), Is.EqualTo(new[] { 1, 2 }));
		Assert.That(viaRightList[20].Right.Select(b => b.Id), Is.EqualTo(new[] { 1 }),
			"right-list JoinMany over overlapping buckets delivers the shared book to both of its tags");

		var viaCollection = tags.Query().JoinManyCollection(books, tagSymIdx).Execute().ToDictionary(r => r.Left.Id);
		Assert.That(viaCollection[10].Right.Select(b => b.Id).OrderBy(i => i), Is.EqualTo(new[] { 1, 2 }));
		Assert.That(viaCollection[20].Right.Select(b => b.Id), Is.EqualTo(new[] { 1 }), "the collection resolver delivers the shared book to both tags");
	}
}
