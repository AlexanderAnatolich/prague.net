namespace Prague.Generated.Tests.Join;

using Prague.Core;
using NUnit.Framework;

// Forward JoinWith{T} for NON-SELECTOR one-to-one foreign keys (#92). Before, a
// [DataCacheForeignKey<Author>(OneToOne)] emitted only the reverse method on the target cache
// (AuthorCache.JoinWithAuthorProfile), never the forward one, so JoinOneLeftUniqueIndexResolver was
// reachable only through the selector form. Two shapes are emitted now, and each one has to match
// the hand-written lower-level join over the very same index:
//   AuthorProfile.AuthorId (non-PK)     → JoinOne(AuthorIdIndex, authors)  — left unique index
//   ProductEventExtension.EventId (PK)  → JoinOne(events)                  — PK-to-PK, no index step
// The fixtures are the pre-existing ForeignKeyJoinModels ones: the point of the change is that those
// declarations, unedited, now carry a forward join.
[TestFixture]
public class ForwardOneToOneFkJoinTests {
	private DataCacheRegistry _registry = null!;
	private AuthorCache _authors = null!;
	private AuthorProfileCache _profiles = null!;
	private ProductEventCache _events = null!;
	private ProductEventExtensionCache _extensions = null!;

	[SetUp]
	public void SetUp() {
		_registry = new DataCacheRegistryBuilder()
			.Register<AuthorCache>()
			.Register<AuthorProfileCache>()
			.Register<BookCache>()
			.Register<BookReviewCache>()
			.Register<AuthorAwardCache>()
			.Register<AuthorPublisherCache>()
			.Register<AuthorEventCache>()
			.Register<ProductEventCache>()
			.Register<ProductEventExtensionCache>()
			.Build();
		_authors = _registry.GetCache<AuthorCache>();
		_profiles = _registry.GetCache<AuthorProfileCache>();
		_events = _registry.GetCache<ProductEventCache>();
		_extensions = _registry.GetCache<ProductEventExtensionCache>();

		// Authors 0..7 exist; profiles 0..9 — so profiles 8 and 9 point at an author that does not.
		for (var a = 0; a < 8; a++)
			_authors.AddOrUpdate(new Author { Id = a, Name = $"A{a}", Country = a % 2 == 0 ? "UK" : "US" });
		for (var p = 0; p < 10; p++)
			_profiles.AddOrUpdate(new AuthorProfile { Id = p, AuthorId = p, Bio = $"bio{p}", Website = p % 2 == 0 ? "w" : "" });

		// Events 0..5 exist; extensions 0..7 — extensions 6 and 7 have no event.
		for (var e = 0; e < 6; e++)
			_events.AddOrUpdate(new ProductEvent { Id = e, Name = $"E{e}" });
		for (var x = 0; x < 8; x++)
			_extensions.AddOrUpdate(new ProductEventExtension { EventId = x, ExtraData = $"x{x}", Priority = x % 3 });
	}

	private static string ProfileRow(JoinResult<AuthorProfile, Author?> r)
		=> $"{r.Left.Id}|{(r.Right is null ? "-" : r.Right.Id.ToString())}";

	private static string ExtensionRow(JoinResult<ProductEventExtension, ProductEvent?> r)
		=> $"{r.Left.EventId}|{(r.Right is null ? "-" : r.Right.Id.ToString())}";

	private static void AssertSameRows<T>(QueryResults<T> expected, QueryResults<T> actual, Func<T, string> row) {
		try {
			Assert.That(actual.Count, Is.EqualTo(expected.Count), "Count");
			Assert.That(actual.TotalCount, Is.EqualTo(expected.TotalCount), "TotalCount");
			var e = new string[expected.Count];
			var a = new string[actual.Count];
			for (var i = 0; i < e.Length; i++) e[i] = row(expected[i]);
			for (var i = 0; i < a.Length; i++) a[i] = row(actual[i]);
			Array.Sort(e, StringComparer.Ordinal);
			Array.Sort(a, StringComparer.Ordinal);
			Assert.That(a, Is.EqualTo(e).AsCollection, "row set");
		} finally {
			expected.Dispose();
			actual.Dispose();
		}
	}

	// ── Non-PK one-to-one: the emitted sugar is the hand-written left-unique-index join ──

	[Test]
	public void JoinWithAuthor_EqualsHandWrittenJoinOneOverTheSameIndex() {
		AssertSameRows(
			_profiles.Cache.Query().JoinOne(_profiles.AuthorIdIndex, _authors).Execute(),
			_profiles.Query().JoinWithAuthor().Execute(),
			ProfileRow);
	}

	[Test]
	public void InnerJoinWithAuthor_EqualsHandWrittenInnerJoinOne_AndDropsTheAuthorlessProfiles() {
		AssertSameRows(
			_profiles.Cache.Query().InnerJoinOne(_profiles.AuthorIdIndex, _authors).Execute(),
			_profiles.Query().InnerJoinWithAuthor().Execute(),
			ProfileRow);

		using var outer = _profiles.Query().JoinWithAuthor().Execute();
		using var inner = _profiles.Query().InnerJoinWithAuthor().Execute();
		Assert.That(outer.Count, Is.EqualTo(10));
		Assert.That(inner.Count, Is.EqualTo(8), "profiles 8 and 9 have no author");
		for (var i = 0; i < inner.Count; i++)
			Assert.That(inner[i].Right, Is.Not.Null);
	}

	[Test]
	public void JoinWithAuthor_Filtered_NarrowsTheJoinedInRight() {
		AssertSameRows(
			_profiles.Cache.Query().JoinOne(_profiles.AuthorIdIndex, _authors, static q => q.Where(static a => a.Country == "UK")).Execute(),
			_profiles.Query().JoinWithAuthor(static q => q.Where(static a => a.Country == "UK")).Execute(),
			ProfileRow);

		using var rows = _profiles.Query().JoinWithAuthor(static q => q.Where(static a => a.Country == "UK")).Execute();
		for (var i = 0; i < rows.Count; i++)
			if (rows[i].Right is { } author)
				Assert.That(author.Country, Is.EqualTo("UK"));
	}

	[Test]
	public void JoinWithAuthor_FilteredWithArg_NarrowsTheJoinedInRight() {
		AssertSameRows(
			_profiles.Cache.Query().JoinOne(_profiles.AuthorIdIndex, _authors, static (q, c) => q.Where(a => a.Country == c), "US").Execute(),
			_profiles.Query().JoinWithAuthor(static (q, c) => q.Where(a => a.Country == c), "US").Execute(),
			ProfileRow);
	}

	[Test]
	public void InnerJoinWithAuthor_FilteredFlavors_EqualHandWritten() {
		AssertSameRows(
			_profiles.Cache.Query().InnerJoinOne(_profiles.AuthorIdIndex, _authors, static q => q.Where(static a => a.Country == "UK")).Execute(),
			_profiles.Query().InnerJoinWithAuthor(static q => q.Where(static a => a.Country == "UK")).Execute(),
			ProfileRow);
		AssertSameRows(
			_profiles.Cache.Query().InnerJoinOne(_profiles.AuthorIdIndex, _authors, static (q, c) => q.Where(a => a.Country == c), "US").Execute(),
			_profiles.Query().InnerJoinWithAuthor(static (q, c) => q.Where(a => a.Country == c), "US").Execute(),
			ProfileRow);
	}

	// ── FK on the primary key: PK-to-PK, no index step ───────────────────────

	[Test]
	public void JoinWithProductEvent_OnAPkForeignKey_EqualsHandWrittenPkToPkJoinOne() {
		AssertSameRows(
			_extensions.Cache.Query().JoinOne(_events).Execute(),
			_extensions.Query().JoinWithProductEvent().Execute(),
			ExtensionRow);
	}

	[Test]
	public void InnerJoinWithProductEvent_OnAPkForeignKey_DropsTheEventlessExtensions() {
		AssertSameRows(
			_extensions.Cache.Query().InnerJoinOne(_events).Execute(),
			_extensions.Query().InnerJoinWithProductEvent().Execute(),
			ExtensionRow);

		using var outer = _extensions.Query().JoinWithProductEvent().Execute();
		using var inner = _extensions.Query().InnerJoinWithProductEvent().Execute();
		Assert.That(outer.Count, Is.EqualTo(8));
		Assert.That(inner.Count, Is.EqualTo(6), "extensions 6 and 7 have no event");
	}

	// ── Sort → JoinWith ──────────────────────────────────────────────────────

	private readonly struct ProfileByIdDesc : IComparer<AuthorProfile> {
		public int Compare(AuthorProfile? x, AuthorProfile? y) => (y?.Id ?? 0).CompareTo(x?.Id ?? 0);
	}

	[Test]
	public void SortThenJoinWithAuthor_OrdersTheLeftsAndKeepsTheJoin() {
		var cmp = new ProfileByIdDesc();
		using var rows = _profiles.Query().Sort(cmp).JoinWithAuthor().Execute();
		Assert.That(rows.Count, Is.EqualTo(10));
		for (var i = 1; i < rows.Count; i++)
			Assert.That(rows[i].Left.Id, Is.LessThan(rows[i - 1].Left.Id), "descending by profile id");
		for (var i = 0; i < rows.Count; i++) {
			var expected = rows[i].Left.AuthorId < 8 ? rows[i].Left.AuthorId : (int?)null;
			Assert.That(rows[i].Right?.Id, Is.EqualTo(expected));
		}
	}

	[Test]
	public void SortBoundedThenJoinWithAuthor_PagesTheSortedLefts() {
		var cmp = new ProfileByIdDesc();
		using var page = _profiles.Query().SortBounded(cmp).JoinWithAuthor().Execute(0, 3);
		Assert.That(page.Count, Is.EqualTo(3));
		Assert.That(page[0].Left.Id, Is.EqualTo(9));
		Assert.That(page[1].Left.Id, Is.EqualTo(8));
		Assert.That(page[2].Left.Id, Is.EqualTo(7));
		Assert.That(page[0].Right, Is.Null, "profile 9 has no author");
		Assert.That(page[2].Right!.Id, Is.EqualTo(7));
	}

	// ── Pooled results: the pooled and the allocating paths agree, and repeated
	//    rent/Dispose rounds stay correct (a double return would corrupt the pool).

	[Test]
	public void PooledForwardJoins_AgreeWithAllocating_AcrossRepeatedRounds() {
		for (var round = 0; round < 50; round++) {
			AssertSameRows(_profiles.Query().JoinWithAuthor().Execute(), _profiles.Query().JoinWithAuthor().ExecutePooledCloned(), ProfileRow);
			AssertSameRows(_profiles.Query().InnerJoinWithAuthor().Execute(), _profiles.Query().InnerJoinWithAuthor().ExecutePooled(), ProfileRow);
			AssertSameRows(_extensions.Query().JoinWithProductEvent().Execute(), _extensions.Query().JoinWithProductEvent().ExecutePooledCloned(), ExtensionRow);
			AssertSameRows(_extensions.Query().InnerJoinWithProductEvent().Execute(), _extensions.Query().InnerJoinWithProductEvent().ExecutePooled(), ExtensionRow);
		}
	}

	[Test]
	public void ForwardJoinCounts_MatchTheRowCounts() {
		Assert.Multiple(() => {
			Assert.That(_profiles.Query().JoinWithAuthor().Count(), Is.EqualTo(10));
			Assert.That(_profiles.Query().InnerJoinWithAuthor().Count(), Is.EqualTo(8));
			Assert.That(_extensions.Query().JoinWithProductEvent().Count(), Is.EqualTo(8));
			Assert.That(_extensions.Query().InnerJoinWithProductEvent().Count(), Is.EqualTo(6));
		});
	}

	// The auto index for a non-PK one-to-one FK is the symmetric variant now — the forward join
	// reads its .Reverse — while a one-to-one FK ON the primary key keeps the plain unique index
	// (PK-to-PK needs no reverse map).
	[Test]
	public void OneToOneFkIndexShapes_AreSymmetricOffThePk_AndPlainOnIt() {
		Assert.That(_profiles.AuthorIdIndex, Is.InstanceOf<CacheSymmetricUniqueIndex<int, AuthorProfile, int>>());
		Assert.That(_extensions.EventIdIndex, Is.Not.InstanceOf<CacheSymmetricUniqueIndex<long, ProductEventExtension, long>>());
	}

	// The index stays usable as a plain standalone filter.
	[Test]
	public void WithAuthorId_StillFiltersWithoutAJoin() {
		using var rows = _profiles.Query().WithAuthorId(3).Execute();
		Assert.That(rows.Count, Is.EqualTo(1));
		Assert.That(rows[0].Id, Is.EqualTo(3));
	}
}
