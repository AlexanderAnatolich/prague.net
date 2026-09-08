namespace Prague.Core.Tests.Join;

using Prague.Core;

// Bounded top-K over JOINED sorted chains. Sorted Execute* terminals bound transparently when
// take is finite, so every case compares the paged call against the unbounded call — which
// always runs the classic full-sort pipeline — sliced in the test. Entities are the shared Sn*
// fixtures from SortJoinCoreTests (authors without profile/info => inner-join narrowing and
// null-Right paths are covered).
[TestFixture]
public class TopKJoinedCoreTests {
	private InMemoryDataCache<int, SnAuthor> _authors = null!;
	private InMemoryDataCache<int, SnBook> _books = null!;
	private InMemoryDataCache<int, SnProfile> _profiles = null!;
	private InMemoryDataCache<int, SnInfo> _infos = null!;

	private CacheKeyValueListIndex<int, SnBook, int> _bookAuthorIdx = null!;
	private CacheKeyValueIndex<int, SnInfo, int> _infoAuthorIdUniqueIdx = null!;

	[SetUp]
	public void SetUp() {
		_authors = new InMemoryDataCache<int, SnAuthor>();
		_books = new InMemoryDataCache<int, SnBook>();
		_profiles = new InMemoryDataCache<int, SnProfile>();
		_infos = new InMemoryDataCache<int, SnInfo>();

		_bookAuthorIdx = _books.CacheKeyValueListIndex<int>((_, v) => v.AuthorId);
		_infoAuthorIdUniqueIdx = _infos.AddKeyValueIndex<int>((_, v) => v.AuthorId);

		for (var i = 1; i <= 6; i++)
			_authors.AddOrUpdate(i, new SnAuthor { Id = i, Name = $"Author {i}" });

		// Profiles share the author PK: authors 1..4 have one, 5 and 6 do not
		// => InnerJoinOne(_profiles) drops 5 and 6 from the result AND from TotalCount.
		for (var i = 1; i <= 4; i++)
			_profiles.AddOrUpdate(i, new SnProfile { Id = i, Bio = $"Bio {i}" });

		// Infos carry a unique FK; author 2 has none => null Right on the outer JoinOne.
		_infos.AddOrUpdate(701, new SnInfo { Id = 701, AuthorId = 1, Bio = "Info 1" });
		_infos.AddOrUpdate(703, new SnInfo { Id = 703, AuthorId = 3, Bio = "Info 3" });
		_infos.AddOrUpdate(704, new SnInfo { Id = 704, AuthorId = 4, Bio = "Info 4" });

		// Books: authors 1 and 3 have two each, author 2 one, author 4 none.
		_books.AddOrUpdate(101, new SnBook { Id = 101, AuthorId = 1, Title = "A1-1" });
		_books.AddOrUpdate(102, new SnBook { Id = 102, AuthorId = 1, Title = "A1-2" });
		_books.AddOrUpdate(201, new SnBook { Id = 201, AuthorId = 2, Title = "A2-1" });
		_books.AddOrUpdate(301, new SnBook { Id = 301, AuthorId = 3, Title = "A3-1" });
		_books.AddOrUpdate(302, new SnBook { Id = 302, AuthorId = 3, Title = "A3-2" });
	}

	// ── Inner join only ──────────────────────────────────────────────────────

	[TestCase(0, 2)]
	[TestCase(1, 2)]
	[TestCase(3, 5)]
	[TestCase(4, 5)]   // skip == inner-narrowed total
	[TestCase(9, 5)]   // skip past total
	public void Sorted_InnerJoinOne_Paged_MatchesFullClassicOrder(int skip, int take) {
		using var full = _authors.Query().SortBounded(new AuthorByIdDesc()).InnerJoinOne(_profiles).ExecutePooled();
		var expectedIds = full.Select(r => r.Left.Id).Skip(skip).Take(take).ToArray();
		var expectedBios = full.Select(r => r.Right!.Bio).Skip(skip).Take(take).ToArray();

		using var paged = _authors.Query().SortBounded(new AuthorByIdDesc()).InnerJoinOne(_profiles).ExecutePooled(skip, take);

		// Authors 5 and 6 have no profile: inner narrowing drops them everywhere.
		Assert.That(full.Count, Is.EqualTo(4), "fixture sanity: inner join narrows to 4 authors");
		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		Assert.That(paged.Count, Is.EqualTo(expectedIds.Length));
		Assert.That(paged.Select(r => r.Left.Id).ToArray(), Is.EqualTo(expectedIds));
		Assert.That(paged.Select(r => r.Right!.Bio).ToArray(), Is.EqualTo(expectedBios));
	}

	// ── Full BDR-like shape: inner join + outer one + many ────────────────────

	[TestCase(0, 2)]
	[TestCase(1, 2)]
	[TestCase(2, 10)]
	public void Sorted_InnerJoin_JoinOne_JoinMany_Paged_MatchesFullClassicOrder(int skip, int take) {
		using var full = _authors.Query()
			.SortBounded(new AuthorByIdDesc())
			.InnerJoinOne(_profiles)
			.JoinOne(_infos, _infoAuthorIdUniqueIdx)
			.JoinMany(_books, _bookAuthorIdx)
			.ExecutePooled();
		var expectedIds = full.Select(r => r.Left.Id).Skip(skip).Take(take).ToArray();
		var expectedBios = full.Select(r => r.Right!.Bio).Skip(skip).Take(take).ToArray();
		var expectedInfos = full.Select(r => r.Right2?.Bio).Skip(skip).Take(take).ToArray();
		var expectedBooks = full.Select(r => r.Right3.Select(b => b.Id).OrderBy(id => id).ToArray())
			.Skip(skip).Take(take).ToArray();

		using var paged = _authors.Query()
			.SortBounded(new AuthorByIdDesc())
			.InnerJoinOne(_profiles)
			.JoinOne(_infos, _infoAuthorIdUniqueIdx)
			.JoinMany(_books, _bookAuthorIdx)
			.ExecutePooled(skip, take);

		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		Assert.That(paged.Count, Is.EqualTo(expectedIds.Length));
		Assert.That(paged.Select(r => r.Left.Id).ToArray(), Is.EqualTo(expectedIds));
		Assert.That(paged.Select(r => r.Right!.Bio).ToArray(), Is.EqualTo(expectedBios));
		// Outer JoinOne: author 2 has no info => null Right2 must be preserved.
		Assert.That(paged.Select(r => r.Right2?.Bio).ToArray(), Is.EqualTo(expectedInfos));
		// JoinMany children per row, in the same order.
		var pagedBooks = paged.Select(r => r.Right3.Select(b => b.Id).OrderBy(id => id).ToArray()).ToArray();
		Assert.That(pagedBooks, Is.EqualTo(expectedBooks));
	}

	// ── Outer joins only (no inner narrowing) ────────────────────────────────

	[Test]
	public void Sorted_JoinManyOnly_Paged_MatchesFullClassicOrder() {
		using var full = _authors.Query().SortBounded(new AuthorByIdDesc()).JoinMany(_books, _bookAuthorIdx).ExecutePooled();
		var expectedIds = full.Select(r => r.Left.Id).Skip(1).Take(3).ToArray();
		var expectedBooks = full.Select(r => r.Right.Select(b => b.Id).OrderBy(id => id).ToArray())
			.Skip(1).Take(3).ToArray();

		using var paged = _authors.Query().SortBounded(new AuthorByIdDesc()).JoinMany(_books, _bookAuthorIdx).ExecutePooled(1, 3);

		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		Assert.That(paged.Select(r => r.Left.Id).ToArray(), Is.EqualTo(expectedIds));
		var pagedBooks = paged.Select(r => r.Right.Select(b => b.Id).OrderBy(id => id).ToArray()).ToArray();
		Assert.That(pagedBooks, Is.EqualTo(expectedBooks));
	}

	// ── Where + inner join: TotalCount is the filtered, narrowed total ────────

	[Test]
	public void Sorted_Where_InnerJoin_Paged_TotalCountIsFilteredNarrowedTotal() {
		using var full = _authors.Query()
			.Where(static a => a.Id <= 3)
			.SortBounded(new AuthorByIdDesc())
			.InnerJoinOne(_profiles)
			.ExecutePooled();
		var expectedIds = full.Select(r => r.Left.Id).Take(2).ToArray();

		using var paged = _authors.Query()
			.Where(static a => a.Id <= 3)
			.SortBounded(new AuthorByIdDesc())
			.InnerJoinOne(_profiles)
			.ExecutePooled(0, 2);

		Assert.That(full.Count, Is.EqualTo(3), "fixture sanity: Where(<=3) x inner join => 3 rows");
		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		Assert.That(paged.Select(r => r.Left.Id).ToArray(), Is.EqualTo(expectedIds));
	}

	// ── Fallback shape (post-join sorter) must still page correctly ──────────

	[Test]
	public void PostJoinSorter_Paged_MatchesFullClassicOrder() {
		// Sorter declared AFTER the join => not innermost => the bounded plan declines and the
		// classic pipeline runs; paging must still match the full order.
		using var full = _authors.Query()
			.JoinMany(_books, _bookAuthorIdx)
			.SortBounded(new JoinedByLeftIdDesc<QueryResults<SnBook>>())
			.ExecutePooled();
		var expectedIds = full.Select(r => r.Left.Id).Skip(1).Take(2).ToArray();

		using var paged = _authors.Query()
			.JoinMany(_books, _bookAuthorIdx)
			.SortBounded(new JoinedByLeftIdDesc<QueryResults<SnBook>>())
			.ExecutePooled(1, 2);

		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		Assert.That(paged.Select(r => r.Left.Id).ToArray(), Is.EqualTo(expectedIds));
	}

	// ── Filtered inner join: filter runs once, results match classic ─────────

	[Test]
	public void FilteredInnerJoin_Paged_MatchesFullClassicOrder() {
		using var full = _authors.Query()
			.SortBounded(new AuthorByIdDesc())
			.InnerJoinOne(_profiles, q => q.Where(static p => p.Id != 2))
			.ExecutePooled();
		var expectedIds = full.Select(r => r.Left.Id).Take(3).ToArray();
		var expectedRights = full.Select(r => r.Right!.Id).Take(3).ToArray();

		using var paged = _authors.Query()
			.SortBounded(new AuthorByIdDesc())
			.InnerJoinOne(_profiles, q => q.Where(static p => p.Id != 2))
			.ExecutePooled(0, 3);

		Assert.That(full.Count, Is.EqualTo(3), "fixture sanity: profiles 1,3,4 survive the filter");
		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		Assert.That(paged.Select(r => r.Left.Id).ToArray(), Is.EqualTo(expectedIds));
		Assert.That(paged.Select(r => r.Right!.Id).ToArray(), Is.EqualTo(expectedRights));
	}

	[Test]
	public void FilteredInnerJoin_Paged_InvokesUserFilterOnceEachRow_LikeClassic() {
		// The bounded plan runs two passes (narrow, then fill). The fill must NOT re-apply the
		// user's filter, otherwise a non-deterministic predicate diverges and the lambda is
		// charged twice per query. The unbounded call is the classic single-pass baseline.
		var pagedCalls = 0;
		using (var paged = _authors.Query()
			       .SortBounded(new AuthorByIdDesc())
			       .InnerJoinOne(_profiles, q => q.Where(p => { pagedCalls++; return p.Id != 2; }))
			       .ExecutePooled(0, 3)) {
			Assert.That(paged.Count, Is.EqualTo(3));
		}

		var classicCalls = 0;
		using (var full = _authors.Query()
			       .SortBounded(new AuthorByIdDesc())
			       .InnerJoinOne(_profiles, q => q.Where(p => { classicCalls++; return p.Id != 2; }))
			       .ExecutePooled()) {
			Assert.That(full.Count, Is.EqualTo(3));
		}

		Assert.That(pagedCalls, Is.EqualTo(classicCalls),
			$"paged invoked the join filter {pagedCalls} times vs classic {classicCalls}");
	}

	[Test]
	public void InnerJoin_Paged_NeverReturnsNullRight() {
		// Inner-join invariant: a surviving row always carries its right value. The bounded plan
		// resolves the right side in a second pass, so this pins the post-fill prune.
		using var paged = _authors.Query()
			.SortBounded(new AuthorByIdDesc())
			.InnerJoinOne(_profiles)
			.ExecutePooled(0, 10);

		Assert.That(paged.Count, Is.EqualTo(4));
		foreach (var row in paged)
			Assert.That(row.Right, Is.Not.Null, $"inner join row {row.Left.Id} must carry a right value");
	}

	// ── Cloning ──────────────────────────────────────────────────────────────

	[Test]
	public void PagedPooledCloned_SurvivorsAreClones_JoinSlotsIntact() {
		using var cloned = _authors.Query()
			.SortBounded(new AuthorByIdDesc())
			.InnerJoinOne(_profiles)
			.ExecutePooledCloned(0, 2);
		using var live = _authors.Query()
			.SortBounded(new AuthorByIdDesc())
			.InnerJoinOne(_profiles)
			.ExecutePooled(0, 2);

		Assert.That(cloned.Count, Is.EqualTo(live.Count));
		for (var i = 0; i < cloned.Count; i++) {
			Assert.That(ReferenceEquals(cloned[i].Left, live[i].Left), Is.False, "cloned Left must not alias the cache instance");
			Assert.That(cloned[i].Left.CacheEquals(live[i].Left), Is.True);
			Assert.That(ReferenceEquals(cloned[i].Right, live[i].Right), Is.False, "cloned Right must not alias the cache instance");
			Assert.That(cloned[i].Right!.CacheEquals(live[i].Right), Is.True);
		}
	}

	// ── Ties: bounded page must still be a valid top-K ───────────────────────

	private sealed class AllEqual : IComparer<SnAuthor> {
		public int Compare(SnAuthor? x, SnAuthor? y) => 0;
	}

	[Test]
	public void Ties_PagedResultIsValidTopK() {
		using var paged = _authors.Query().SortBounded(new AllEqual()).InnerJoinOne(_profiles).ExecutePooled(0, 2);
		using var full = _authors.Query().SortBounded(new AllEqual()).InnerJoinOne(_profiles).ExecutePooled();

		Assert.That(paged.Count, Is.EqualTo(2));
		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		// Rows may differ under an all-equal comparer; every returned row must still be a real,
		// inner-join-surviving author.
		foreach (var row in paged) {
			Assert.That(row.Left.Id, Is.LessThanOrEqualTo(4), "inner join survivors only");
			Assert.That(row.Right, Is.Not.Null);
		}
	}
}
