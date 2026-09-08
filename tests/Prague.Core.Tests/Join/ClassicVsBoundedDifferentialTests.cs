namespace Prague.Core.Tests.Join;

using Prague.Core;

// The bounded top-K plan and the classic full-sort pipeline serve the same public terminals —
// which one runs depends only on whether `take` is finite. They must therefore agree on every
// observable: which rows come back, in which order, and what TotalCount says. These are the
// two shapes where they historically did not.
[TestFixture]
public class ClassicVsBoundedDifferentialTests {
	private InMemoryDataCache<int, SnAuthor> _authors = null!;
	private InMemoryDataCache<int, SnProfile> _profiles = null!;

	// Span.Sort falls back to insertion sort — which is stable — below 16 elements, so a tie
	// divergence only shows once introsort actually runs. TieCount is comfortably past that.
	private const int TieCount = 64;

	private InMemoryDataCache<int, SnAuthor> _many = null!;

	[SetUp]
	public void SetUp() {
		_authors = new InMemoryDataCache<int, SnAuthor>();
		_profiles = new InMemoryDataCache<int, SnProfile>();
		_many = new InMemoryDataCache<int, SnAuthor>();

		for (var i = 1; i <= 6; i++)
			_authors.AddOrUpdate(i, new SnAuthor { Id = i, Name = $"Author {i}" });

		// Every author has a profile, so the inner join narrows nothing: the only thing that
		// can drop a row here is the Where.
		for (var i = 1; i <= 6; i++)
			_profiles.AddOrUpdate(i, new SnProfile { Id = i, Bio = $"Bio {i}" });

		for (var i = 1; i <= TieCount; i++)
			_many.AddOrUpdate(i, new SnAuthor { Id = i, Name = $"Author {i}" });
	}

	// Index-seeded candidates skip the seed-time filter pass, so the classic joined-inner
	// pipeline used to retain a dictionary slot for every candidate with a right match —
	// including the ones the Where rejects. Their Left was never written, leaving rows with a
	// default Left, and Count > TotalCount. The bounded plan materializes only rows the base
	// walk emitted, so it never produced them.
	[TestCase(0, 100)]
	[TestCase(0, 2)]
	[TestCase(1, 2)]
	public void IndexSeeded_Where_InnerJoin_HasNoPhantomRows(int skip, int take) {
		var keys = new[] { 1, 2, 3, 4, 5, 6 };

		using var classic = _authors.Query()
			.UseIndex(_authors.KeyIndex, keys)
			.Where(a => a.Id <= 3)
			.Sort(new AuthorByIdDesc())
			.InnerJoinOne(_profiles)
			.ExecutePooled();

		using var bounded = _authors.Query()
			.UseIndex(_authors.KeyIndex, keys)
			.Where(a => a.Id <= 3)
			.Sort(new AuthorByIdDesc())
			.InnerJoinOne(_profiles)
			.ExecutePooled(skip, take);

		Assert.That(classic.TotalCount, Is.EqualTo(3), "Where keeps authors 1..3, all of which have a profile");
		Assert.That(classic.Count, Is.LessThanOrEqualTo(classic.TotalCount), "a page can never hold more rows than the total");
		foreach (var row in classic)
			Assert.That(row.Left, Is.Not.Null, "classic path emitted a row whose Left was never written");

		var expected = classic.Select(r => r.Left.Id).Skip(skip).Take(take).ToArray();

		Assert.That(bounded.TotalCount, Is.EqualTo(classic.TotalCount));
		Assert.That(bounded.Select(r => r.Left.Id).ToArray(), Is.EqualTo(expected));
		foreach (var row in bounded)
			Assert.That(row.Left, Is.Not.Null);
	}

	// Same shape without the Sort: the phantom is a property of index-seeded candidates plus a
	// Where plus an inner join, so it is reachable with no sorter at all — a shape the bounded
	// plan never handles.
	[Test]
	public void IndexSeeded_Where_InnerJoin_Unsorted_HasNoPhantomRows() {
		var keys = new[] { 1, 2, 3, 4, 5, 6 };

		using var results = _authors.Query()
			.UseIndex(_authors.KeyIndex, keys)
			.Where(a => a.Id <= 3)
			.InnerJoinOne(_profiles)
			.ExecutePooled();

		Assert.That(results.TotalCount, Is.EqualTo(3));
		Assert.That(results.Count, Is.EqualTo(3));
		foreach (var row in results)
			Assert.That(row.Left, Is.Not.Null);
	}

	// Ties: the bounded plan carries an encounter ordinal so consecutive pages partition the
	// result. The classic sort has to break ties the same way, or Execute() and Execute(0, N)
	// disagree on the order of equal rows over identical data.
	[TestCase(0, TieCount)]
	[TestCase(0, 8)]
	[TestCase(8, 8)]
	[TestCase(TieCount - 4, 4)]
	[TestCase(TieCount / 2, 1)]
	public void TieOrder_AgreesBetweenClassicAndBounded(int skip, int take) {
		using var classic = _many.Query().Sort(new AuthorByParity()).ExecutePooled();
		var expected = classic.Select(a => a.Id).Skip(skip).Take(take).ToArray();

		using var bounded = _many.Query().Sort(new AuthorByParity()).ExecutePooled(skip, take);

		Assert.That(bounded.TotalCount, Is.EqualTo(classic.Count));
		Assert.That(bounded.Select(a => a.Id).ToArray(), Is.EqualTo(expected));
	}

	// Consecutive bounded pages must partition the result exactly — no duplicates, no gaps —
	// which is the property the ordinal tiebreak exists to provide.
	[Test]
	public void TiedPages_PartitionTheResult() {
		var seen = new List<int>();
		for (var skip = 0; skip < TieCount; skip += 8) {
			using var page = _many.Query().Sort(new AuthorByParity()).ExecutePooled(skip, 8);
			seen.AddRange(page.Select(a => a.Id));
		}

		Assert.That(seen.Count, Is.EqualTo(TieCount));
		Assert.That(seen.Distinct().Count(), Is.EqualTo(TieCount), "pages overlapped");
		Assert.That(seen.OrderBy(id => id).ToArray(), Is.EqualTo(Enumerable.Range(1, TieCount).ToArray()));
	}
}

// Every even Id ties with every other even Id, and likewise for odd — six rows, two tie groups.
internal sealed class AuthorByParity : IComparer<SnAuthor> {
	public int Compare(SnAuthor? x, SnAuthor? y) => (x?.Id % 2 ?? 0).CompareTo(y?.Id % 2 ?? 0);
}
