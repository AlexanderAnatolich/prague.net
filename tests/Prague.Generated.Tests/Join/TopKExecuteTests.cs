namespace Prague.Generated.Tests.Join;

using System.Collections.Generic;
using System.Linq;
using Prague.Core;
using NUnit.Framework;

// ── Transparently bounded paging at the codegen layer ─────────────────────────
//
// Sorted Execute* terminals bound the page with a heap when take is finite, so the generated
// Query()/JoinWith{X} surface must produce classic-identical pages. The oracle is the unbounded
// call (take == int.MaxValue always runs the classic full-sort pipeline) sliced in the test.
// Reuses the shared Author / Book / AuthorProfile models from ForeignKeyJoinModels.cs
// (authors 3 and 4 have no profile).

[TestFixture]
public class TopKExecuteTests {
	private DataCacheRegistry _registry = null!;
	private AuthorCache _authors = null!;
	private BookCache _books = null!;
	private AuthorProfileCache _profiles = null!;

	[SetUp]
	public void SetUp() {
		_registry = new DataCacheRegistryBuilder()
			.Register<AuthorCache>()
			.Register<BookCache>()
			.Register<AuthorProfileCache>()
			.Build();

		_authors = _registry.GetCache<AuthorCache>();
		_books = _registry.GetCache<BookCache>();
		_profiles = _registry.GetCache<AuthorProfileCache>();

		_authors.AddOrUpdate(new Author { Id = 1, Name = "Tolkien", Country = "UK" });
		_authors.AddOrUpdate(new Author { Id = 2, Name = "Asimov", Country = "US" });
		_authors.AddOrUpdate(new Author { Id = 3, Name = "Lewis", Country = "UK" });
		_authors.AddOrUpdate(new Author { Id = 4, Name = "Herbert", Country = "US" });

		_books.AddOrUpdate(new Book { Id = 101, AuthorId = 1, Title = "Hobbit", Year = 1937 });
		_books.AddOrUpdate(new Book { Id = 102, AuthorId = 1, Title = "LOTR", Year = 1954 });
		_books.AddOrUpdate(new Book { Id = 201, AuthorId = 2, Title = "Foundation", Year = 1951 });
		_books.AddOrUpdate(new Book { Id = 401, AuthorId = 4, Title = "Dune", Year = 1965 });

		_profiles.AddOrUpdate(new AuthorProfile { Id = 1, AuthorId = 1, Bio = "UK fantasy", Website = "" });
		_profiles.AddOrUpdate(new AuthorProfile { Id = 2, AuthorId = 2, Bio = "US sci-fi", Website = "" });
		// no profile for authors 3 and 4
	}

	[Test]
	public void Sorted_Simple_PagedPooled_MatchesFullClassicOrder() {
		using var full = _authors.Query().SortBounded(new AuthorByIdDesc()).ExecutePooled();
		var expected = full.Select(a => a.Id).Skip(1).Take(2).ToArray();

		using var paged = _authors.Query().SortBounded(new AuthorByIdDesc()).ExecutePooled(1, 2);

		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		Assert.That(paged.Select(a => a.Id).ToArray(), Is.EqualTo(expected));
	}

	[Test]
	public void Sorted_JoinWithBook_PagedPooled_MatchesFullClassicOrder() {
		using var full = _authors.Query().SortBounded(new AuthorByIdDesc()).JoinWithBook().ExecutePooled();
		var expectedIds = full.Select(r => r.Left.Id).Take(2).ToArray();
		var expectedBooks = full.Select(r => r.Right.Select(b => b.Id).OrderBy(id => id).ToArray()).Take(2).ToArray();

		using var paged = _authors.Query().SortBounded(new AuthorByIdDesc()).JoinWithBook().ExecutePooled(0, 2);

		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		Assert.That(paged.Select(r => r.Left.Id).ToArray(), Is.EqualTo(expectedIds));
		var pagedBooks = paged.Select(r => r.Right.Select(b => b.Id).OrderBy(id => id).ToArray()).ToArray();
		Assert.That(pagedBooks, Is.EqualTo(expectedBooks));
	}

	[Test]
	public void Sorted_JoinWithAuthorProfile_PagedPooled_MatchesFullClassicOrder() {
		using var full = _authors.Query().SortBounded(new AuthorByIdDesc()).JoinWithAuthorProfile().ExecutePooled();
		var expectedIds = full.Select(r => r.Left.Id).Skip(1).Take(2).ToArray();
		var expectedBios = full.Select(r => r.Right?.Bio).Skip(1).Take(2).ToArray();

		using var paged = _authors.Query().SortBounded(new AuthorByIdDesc()).JoinWithAuthorProfile().ExecutePooled(1, 2);

		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		Assert.That(paged.Select(r => r.Left.Id).ToArray(), Is.EqualTo(expectedIds));
		// Authors without a profile keep a null Right on both paths.
		Assert.That(paged.Select(r => r.Right?.Bio).ToArray(), Is.EqualTo(expectedBios));
	}

	[Test]
	public void Sorted_WithKeyFilter_Paged_MatchesFullClassicOrder() {
		var ids = new List<int> { 1, 2, 4 };
		using var full = _authors.Query().WithId(ids).SortBounded(new AuthorByIdDesc()).ExecutePooled();
		var expected = full.Select(a => a.Id).Take(2).ToArray();

		using var paged = _authors.Query().WithId(ids).SortBounded(new AuthorByIdDesc()).ExecutePooled(0, 2);

		Assert.That(full.Count, Is.EqualTo(3), "fixture sanity: key filter narrows to 3 authors");
		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		Assert.That(paged.Select(a => a.Id).ToArray(), Is.EqualTo(expected));
	}

	[Test]
	public void Sorted_Where_Paged_MatchesFullClassicOrder() {
		using var full = _authors.Query().Where(static a => a.Country == "UK").SortBounded(new AuthorByIdDesc()).ExecutePooled();
		var expected = full.Select(a => a.Id).Take(1).ToArray();

		using var paged = _authors.Query().Where(static a => a.Country == "UK").SortBounded(new AuthorByIdDesc()).ExecutePooled(0, 1);

		Assert.That(paged.TotalCount, Is.EqualTo(full.Count));
		Assert.That(paged.Select(a => a.Id).ToArray(), Is.EqualTo(expected));
	}
}
