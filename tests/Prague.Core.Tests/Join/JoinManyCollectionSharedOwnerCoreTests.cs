namespace Prague.Core.Tests.Join;

using System.Collections.Generic;
using System.Linq;
using Prague.Core;
using Prague.Core.Tests.Infrastructure;
using NUnit.Framework;

// One owner referenced by thousands of elements is the worst case for the rounds engine: every
// (element, owner) pair carries the same right key, so each pair needs its own round. The walk must
// stay linear in the number of pairs and allocation-free on the pooled path — the first-fit probe that
// restarted at round 0 and boxed the key on every rejected probe cost O(n²) time and 200 MB per query
// at 4096 elements.
[TestFixture]
[NonParallelizable]
public class JoinManyCollectionSharedOwnerCoreTests {
	private const int Elements = 4096;

	private InMemoryDataCache<int, MnTag> _tags = null!;
	private InMemoryDataCache<int, MnTaggedBook> _books = null!;
	private CacheCollectionSymmetricKeyValueListIndex<int, MnTaggedBook, int> _index = null!;

	[OneTimeSetUp]
	public void SetUp() {
		_tags = new InMemoryDataCache<int, MnTag>();
		_books = new InMemoryDataCache<int, MnTaggedBook>();
		_index = _books.CacheCollectionSymmetricKeyValueListIndex<int>((_, b) => b.TagIds);

		var tagIds = new List<int>(Elements);
		for (var i = 1; i <= Elements; i++) {
			_tags.AddOrUpdate(i, new MnTag { Id = i, Name = "tag" + i });
			tagIds.Add(i);
		}

		_books.AddOrUpdate(1, new MnTaggedBook { Id = 1, Title = "Everything", TagIds = tagIds });
	}

	[Test]
	public void OneOwnerManyElements_EveryElementReceivesTheOwnerOnce() {
		using var results = _tags.Query().JoinManyCollection(_books, _index).ExecutePooled();

		Assert.That(results.Count, Is.EqualTo(Elements));
		Assert.That(results.All(r => r.Right.Count == 1 && r.Right[0].Id == 1), Is.True);
	}

	[Test]
	public void OneOwnerManyElements_Inner_EveryElementSurvives() {
		using var results = _tags.Query().InnerJoinManyCollection(_books, _index).ExecutePooled();

		Assert.That(results.Count, Is.EqualTo(Elements));
		Assert.That(results.Sum(r => r.Right.Count), Is.EqualTo(Elements));
	}

	[Test]
	public void OneOwnerManyElements_PooledQuery_DoesNotAllocatePerPair() {
		for (var i = 0; i < 5; i++) {
			using var warm = _tags.Query().JoinManyCollection(_books, _index).ExecutePooled();
		}

		var before = GC.GetAllocatedBytesForCurrentThread();
		var rows = 0;
		for (var i = 0; i < 10; i++) {
			using var results = _tags.Query().JoinManyCollection(_books, _index).ExecutePooled();
			rows += results.Count;
		}

		var perQuery = (GC.GetAllocatedBytesForCurrentThread() - before) / 10;
		Assert.That(rows, Is.EqualTo(Elements * 10));
		// 4096 pairs × 24 bytes per boxed probe × the quadratic probe count was ~200 MB; the pooled path
		// must stay in the low kilobytes regardless of how many elements share the owner.
		Assert.That(perQuery, Is.LessThan(64 * 1024), $"{perQuery} bytes per query");
	}

	[Test]
	public void OneOwnerManyElements_PoolBalanced() =>
		LeakAssert.Balanced(() => {
			using var results = _tags.Query().JoinManyCollection(_books, _index).ExecutePooled();
			Assert.That(results.Count, Is.EqualTo(Elements));
		});
}
