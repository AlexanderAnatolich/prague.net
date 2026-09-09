namespace Prague.Core.Tests.Join;

using Prague.Core;

// Sort and SortBounded are two deliberately different plans, chosen by the caller. What must hold:
//   - neither may invent rows (the phantom-row shape);
//   - with a TOTAL comparer they are indistinguishable — a total order has exactly one sorted
//     permutation, so there is nothing left for the plans to disagree about;
//   - SortBounded's tie guarantee (encounter order) is what makes consecutive pages partition the
//     result, and that is the reason to opt in.
// With a non-total comparer, classic page boundaries are unspecified — asserted here only as far as
// the contract goes: right count, real rows, no phantoms.
[TestFixture]
public class ClassicVsBoundedDifferentialTests {
	// Span.Sort falls back to insertion sort — which is stable — below 16 elements, so tie behaviour
	// only shows once introsort actually runs. TieCount is comfortably past that.
	private const int TieCount = 64;

	private InMemoryDataCache<int, SnAuthor> _authors = null!;
	private InMemoryDataCache<int, SnProfile> _profiles = null!;
	private InMemoryDataCache<int, SnAuthor> _many = null!;
	private InMemoryDataCache<int, SnProfile> _manyProfiles = null!;

	[SetUp]
	public void SetUp() {
		_authors = new InMemoryDataCache<int, SnAuthor>();
		_profiles = new InMemoryDataCache<int, SnProfile>();
		_many = new InMemoryDataCache<int, SnAuthor>();
		_manyProfiles = new InMemoryDataCache<int, SnProfile>();

		for (var i = 1; i <= 6; i++)
			_authors.AddOrUpdate(i, new SnAuthor { Id = i, Name = $"Author {i}" });

		// Every author has a profile, so the inner join narrows nothing: the only thing that can drop
		// a row here is the Where.
		for (var i = 1; i <= 6; i++)
			_profiles.AddOrUpdate(i, new SnProfile { Id = i, Bio = $"Bio {i}" });

		for (var i = 1; i <= TieCount; i++) {
			_many.AddOrUpdate(i, new SnAuthor { Id = i, Name = $"Author {i}" });
			_manyProfiles.AddOrUpdate(i, new SnProfile { Id = i, Bio = $"Bio {i}" });
		}
	}

	// ── Phantom rows: neither plan may invent a row ──────────────────────────

	// Index-seeded candidates used to skip the seed-time filter pass, so the joined-inner pipeline
	// retained a dictionary slot for every candidate with a right match — including the ones the
	// Where rejects. Their Left was never written, leaving rows with a default Left and
	// Count > TotalCount. Reachable through plain Execute() with no paging at all.
	[Test]
	public void IndexSeeded_Where_InnerJoin_Classic_HasNoPhantomRows() {
		using var results = _authors.Query()
			.UseIndex(_authors.KeyIndex, new[] { 1, 2, 3, 4, 5, 6 })
			.Where(a => a.Id <= 3)
			.Sort(new AuthorByIdDesc())
			.InnerJoinOne(_profiles)
			.ExecutePooled();

		Assert.That(results.TotalCount, Is.EqualTo(3));
		Assert.That(results.Count, Is.EqualTo(3), "Count must not exceed TotalCount");
		foreach (var row in results)
			Assert.That(row.Left, Is.Not.Null, "a row's Left was never written");
	}

	[Test]
	public void IndexSeeded_Where_InnerJoin_Unsorted_HasNoPhantomRows() {
		using var results = _authors.Query()
			.UseIndex(_authors.KeyIndex, new[] { 1, 2, 3, 4, 5, 6 })
			.Where(a => a.Id <= 3)
			.InnerJoinOne(_profiles)
			.ExecutePooled();

		Assert.That(results.TotalCount, Is.EqualTo(3));
		Assert.That(results.Count, Is.EqualTo(3), "Count must not exceed TotalCount");
		foreach (var row in results)
			Assert.That(row.Left, Is.Not.Null, "a row's Left was never written");
	}

	[TestCase(0, 100)]
	[TestCase(0, 2)]
	[TestCase(1, 2)]
	public void IndexSeeded_Where_InnerJoin_Bounded_HasNoPhantomRows(int skip, int take) {
		using var results = _authors.Query()
			.UseIndex(_authors.KeyIndex, new[] { 1, 2, 3, 4, 5, 6 })
			.Where(a => a.Id <= 3)
			.SortBounded(new AuthorByIdDesc())
			.InnerJoinOne(_profiles)
			.ExecutePooled(skip, take);

		Assert.That(results.TotalCount, Is.EqualTo(3));
		Assert.That(results.Count, Is.EqualTo(Math.Clamp(3 - skip, 0, take)));
		foreach (var row in results)
			Assert.That(row.Left, Is.Not.Null);
	}

	// ── A total comparer makes the two plans indistinguishable ───────────────

	[TestCase(0, TieCount)]
	[TestCase(0, 8)]
	[TestCase(8, 8)]
	[TestCase(TieCount - 4, 4)]
	[TestCase(TieCount / 2, 1)]
	[TestCase(TieCount + 10, 4)]
	public void TotalComparer_BothPlansReturnTheSamePage(int skip, int take) {
		using var classic = _many.Query().Sort(new AuthorByParityThenId()).ExecutePooled();
		var expected = classic.Select(a => a.Id).Skip(skip).Take(take).ToArray();

		using var bounded = _many.Query().SortBounded(new AuthorByParityThenId()).ExecutePooled(skip, take);

		Assert.That(bounded.TotalCount, Is.EqualTo(classic.Count));
		Assert.That(bounded.Select(a => a.Id).ToArray(), Is.EqualTo(expected));
	}

	[TestCase(0, TieCount)]
	[TestCase(8, 8)]
	[TestCase(TieCount - 4, 4)]
	public void TotalComparer_BothPlansReturnTheSameJoinedPage(int skip, int take) {
		using var classic = _many.Query().Sort(new AuthorByParityThenId()).InnerJoinOne(_manyProfiles).ExecutePooled();
		var expected = classic.Select(r => r.Left.Id).Skip(skip).Take(take).ToArray();

		using var bounded = _many.Query().SortBounded(new AuthorByParityThenId()).InnerJoinOne(_manyProfiles).ExecutePooled(skip, take);

		Assert.That(bounded.TotalCount, Is.EqualTo(classic.Count));
		Assert.That(bounded.Select(r => r.Left.Id).ToArray(), Is.EqualTo(expected));
	}

	// ── SortBounded's tie guarantee: the reason to opt in ────────────────────

	[Test]
	public void SortBounded_TiedPages_PartitionTheResult() {
		var seen = new List<int>();
		for (var skip = 0; skip < TieCount; skip += 8) {
			using var page = _many.Query().SortBounded(new AuthorByParity()).ExecutePooled(skip, 8);
			seen.AddRange(page.Select(a => a.Id));
		}

		Assert.That(seen.Count, Is.EqualTo(TieCount));
		Assert.That(seen.Distinct().Count(), Is.EqualTo(TieCount), "pages overlapped");
		Assert.That(seen.OrderBy(id => id).ToArray(), Is.EqualTo(Enumerable.Range(1, TieCount).ToArray()));
	}

	// The classic plan makes no such promise with a non-total comparer, and this pins only what it
	// does promise: the right number of real rows drawn from the result. Deliberately no assertion
	// about which rows — that is the difference SortBounded exists to remove.
	[TestCase(0, 8)]
	[TestCase(8, 8)]
	public void Sort_TiedPage_IsWellFormedEvenThoughOrderIsUnspecified(int skip, int take) {
		using var page = _many.Query().Sort(new AuthorByParity()).ExecutePooled(skip, take);

		Assert.That(page.TotalCount, Is.EqualTo(TieCount));
		Assert.That(page.Count, Is.EqualTo(take));
		foreach (var author in page)
			Assert.That(author.Id, Is.InRange(1, TieCount));
	}

	// ── The two plans agree on ties ──────────────────────────────────────────

	// This test used to assert the opposite. It read the plans' tie orders differing as evidence that
	// Sort had not silently adopted the bounded plan, and its own comment called that a guard that
	// would lose its power if the two ever coincided. They coincide now on purpose: the classic sort
	// is stable, so both plans break ties by encounter order and the same query returns the same rows
	// whichever plan runs it. The old inequality was pinning a defect — a caller who switched to
	// SortBounded for the 2.7x it buys on a joined query silently got different rows.
	//
	// The original intent — Sort must not take the bounded execution plan without being asked — is now
	// covered where it is actually observable: query.sortTiedLeftThenJoinClassic and
	// query.sortTiedLeftThenJoinBounded sit 3x apart in the perf baseline, so a silent adoption moves
	// a metric.
	[Test]
	public void Sort_AndSortBounded_AgreeOnTieOrder() {
		using var boundedResults = _many.Query().SortBounded(new AuthorByParity()).ExecutePooled(0, TieCount);
		using var classicResults = _many.Query().Sort(new AuthorByParity()).ExecutePooled(0, TieCount);
		var bounded = boundedResults.Select(a => a.Id).ToArray();
		var classic = classicResults.Select(a => a.Id).ToArray();

		Assert.That(bounded.Take(TieCount / 2).All(id => id % 2 == 0), Is.True, "bounded broke the comparer");
		Assert.That(classic.Take(TieCount / 2).All(id => id % 2 == 0), Is.True, "classic broke the comparer");
		Assert.That(classic, Is.EqualTo(bounded),
			"the plans returned different rows for the same query — the classic sort is no longer stable");
	}

	// The strong, deterministic promise of the bounded plan: the page boundaries do not depend on how
	// you slice them. Encounter order itself is the store's iteration order and deliberately not
	// asserted — only that it is consistent.
	[Test]
	public void SortBounded_PagesConcatenateToTheWholeResult() {
		using var whole = _many.Query().SortBounded(new AuthorByParity()).ExecutePooled(0, TieCount);
		var expected = whole.Select(a => a.Id).ToArray();

		var paged = new List<int>();
		for (var skip = 0; skip < TieCount; skip += 8) {
			using var page = _many.Query().SortBounded(new AuthorByParity()).ExecutePooled(skip, 8);
			paged.AddRange(page.Select(a => a.Id));
		}

		Assert.That(paged.ToArray(), Is.EqualTo(expected));
	}
}

// Every even Id ties with every other even Id, and likewise for odd — two tie groups.
internal sealed class AuthorByParity : IComparer<SnAuthor> {
	public int Compare(SnAuthor? x, SnAuthor? y) => (x?.Id % 2 ?? 0).CompareTo(y?.Id % 2 ?? 0);
}

// The same ordering made total by falling back to the primary key, which is what a caller who wants
// deterministic pages out of the classic plan should supply.
internal sealed class AuthorByParityThenId : IComparer<SnAuthor> {
	public int Compare(SnAuthor? x, SnAuthor? y) {
		var order = (x?.Id % 2 ?? 0).CompareTo(y?.Id % 2 ?? 0);
		return order != 0 ? order : (x?.Id ?? 0).CompareTo(y?.Id ?? 0);
	}
}
