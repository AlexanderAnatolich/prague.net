namespace Prague.Core.Tests.Leaks;

using Prague.Core;
using Prague.Core.Tests.Infrastructure;
using Prague.Core.Tests.Join;

// Pool-balance guarantees for the bounded top-K path: the heap buffer and the page
// buffer must go back to the pool on every exit, including comparer throws.
[TestFixture]
[NonParallelizable]
public class TopKLeakTests {
	private InMemoryDataCache<int, TopKExecuteCoreTests.TkItem> _cache = null!;

	[OneTimeSetUp]
	public void SetUp() {
		_cache = new InMemoryDataCache<int, TopKExecuteCoreTests.TkItem>();
		for (var i = 0; i < 300; i++)
			_cache.AddOrUpdate(i, new TopKExecuteCoreTests.TkItem { Id = i, Order = 300 - i });
	}

	[Test]
	public void TopPooled_HappyPath_DisposeReturnsBuffers() =>
		LeakAssert.Balanced(() => {
			using var results = _cache.Query().SortBounded(new TopKExecuteCoreTests.TkByOrderAsc()).ExecutePooled(5, 20);
			Assert.That(results.Count, Is.EqualTo(20));
		});

	[Test]
	public void TopPooled_DoubleDispose_NoDoubleReturn() =>
		LeakAssert.Balanced(() => {
			var results = _cache.Query().SortBounded(new TopKExecuteCoreTests.TkByOrderAsc()).ExecutePooled(0, 10);
			results.Dispose();
			results.Dispose();
		});

	private sealed class ThrowingComparer : IComparer<TopKExecuteCoreTests.TkItem> {
		private int _calls;

		public int Compare(TopKExecuteCoreTests.TkItem? x, TopKExecuteCoreTests.TkItem? y) {
			if (++_calls > 50)
				throw new InvalidOperationException("boom");

			return (x?.Order ?? 0).CompareTo(y?.Order ?? 0);
		}
	}

	[Test]
	public void TopPooled_ComparerThrowsMidWalk_HeapBufferStillReturned() =>
		LeakAssert.Balanced(() => {
			try {
				using var results = _cache.Query().SortBounded(new ThrowingComparer()).ExecutePooled(0, 10);
			} catch (InvalidOperationException) {
				// Expected: the comparer blows up mid-selection; buffers must still be returned.
			}
		});

	// ── Joined bounded path: heap + dictionary + Many child buffers ──────────

	private InMemoryDataCache<int, SnAuthor> _authors = null!;
	private InMemoryDataCache<int, SnProfile> _profiles = null!;
	private InMemoryDataCache<int, SnBook> _books = null!;
	private CacheKeyValueListIndex<int, SnBook, int> _bookAuthorIdx = null!;

	[OneTimeSetUp]
	public void SetUpJoined() {
		_authors = new InMemoryDataCache<int, SnAuthor>();
		_profiles = new InMemoryDataCache<int, SnProfile>();
		_books = new InMemoryDataCache<int, SnBook>();
		_bookAuthorIdx = _books.CacheKeyValueListIndex<int>((_, v) => v.AuthorId);

		for (var i = 1; i <= 50; i++) {
			_authors.AddOrUpdate(i, new SnAuthor { Id = i, Name = $"Author {i}" });
			_profiles.AddOrUpdate(i, new SnProfile { Id = i, Bio = $"Bio {i}" });
			_books.AddOrUpdate(i * 10, new SnBook { Id = i * 10, AuthorId = i, Title = $"Book {i}" });
			_books.AddOrUpdate(i * 10 + 1, new SnBook { Id = i * 10 + 1, AuthorId = i, Title = $"Book {i}b" });
		}
	}

	[Test]
	public void JoinedTopPooled_HappyPath_DisposeReturnsBuffers() =>
		LeakAssert.Balanced(() => {
			using var results = _authors.Query()
				.SortBounded(new AuthorByIdDesc())
				.InnerJoinOne(_profiles)
				.JoinMany(_books, _bookAuthorIdx)
				.ExecutePooled(2, 5);
			Assert.That(results.Count, Is.EqualTo(5));
		});

	[Test]
	public void JoinedTopPooled_DoubleDispose_NoDoubleReturn() =>
		LeakAssert.Balanced(() => {
			var results = _authors.Query()
				.SortBounded(new AuthorByIdDesc())
				.InnerJoinOne(_profiles)
				.JoinMany(_books, _bookAuthorIdx)
				.ExecutePooled(0, 5);
			results.Dispose();
			results.Dispose();
		});

	private sealed class ThrowingAuthorComparer : IComparer<SnAuthor> {
		private int _calls;

		public int Compare(SnAuthor? x, SnAuthor? y) {
			if (++_calls > 20)
				throw new InvalidOperationException("boom");

			return (y?.Id ?? 0).CompareTo(x?.Id ?? 0);
		}
	}

	[Test]
	public void JoinedTopPooled_JoinFilterThrowsDuringNarrow_CandidateSetStillReturned() =>
		// The bounded path auto-populates candidates in the prepare step and only releases them
		// when the base walk runs. The narrow pass sits between the two and executes user code,
		// so a throw there must not strand the candidate set's pooled arrays.
		LeakAssert.Balanced(() => {
			try {
				using var results = _authors.Query()
					.SortBounded(new AuthorByIdDesc())
					.InnerJoinOne(_profiles, q => q.Where(static _ => throw new InvalidOperationException("boom")))
					.ExecutePooled(0, 5);
			} catch (InvalidOperationException) {
				// Expected.
			}
		});

	[Test]
	public void JoinedTopPooled_ComparerThrowsMidWalk_AllBuffersStillReturned() =>
		LeakAssert.Balanced(() => {
			try {
				using var results = _authors.Query()
					.SortBounded(new ThrowingAuthorComparer())
					.InnerJoinOne(_profiles)
					.JoinMany(_books, _bookAuthorIdx)
					.ExecutePooled(0, 5);
			} catch (InvalidOperationException) {
				// Expected: heap, dictionary and any child buffers must all go back to the pool.
			}
		});
}
