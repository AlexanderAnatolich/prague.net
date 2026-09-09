namespace Prague.Core.Tests.Join;

using System.Linq;
using Prague.Core;
using NUnit.Framework;

/// <summary>
///   A right that belongs to more than one left must reach every one of them. The pair set that
///   drives delivery is keyed by the right key, so a right shared across lefts collapses to a single
///   pair and only the left that recorded it first is served — every other left is starved, with no
///   concurrency involved. Reachable two ways: a non-injective key selector mapping several lefts
///   onto one index key, and a collection-backed index whose buckets overlap.
/// </summary>
[TestFixture]
public class JoinManySharedRightCoreTests {
	[Test]
	public void JoinMany_NonInjectiveSelector_EveryLeftGetsTheSharedRights() {
		var authors = new InMemoryDataCache<long, MnAuthor>();
		var books = new InMemoryDataCache<int, MnBook>();
		var authorIdIdx = books.CacheKeyValueListIndex<int>((_, v) => v.AuthorId);

		authors.AddOrUpdate(10L, new MnAuthor { Id = 10, Name = "Ten" });
		authors.AddOrUpdate(11L, new MnAuthor { Id = 11, Name = "Eleven" });
		books.AddOrUpdate(1, new MnBook { Id = 1, AuthorId = 1, Title = "b1" });
		books.AddOrUpdate(2, new MnBook { Id = 2, AuthorId = 1, Title = "b2" });

		// Both lefts select index key 1, so both must receive books 1 and 2.
		var results = authors.Query().JoinMany(static (long k) => 1, books, authorIdIdx).Execute();

		Assert.That(results.Count, Is.EqualTo(2));
		var byId = results.ToDictionary(r => r.Left.Id);
		Assert.Multiple(() => {
			Assert.That(byId[10].Right.Select(b => b.Title).OrderBy(t => t), Is.EqualTo(new[] { "b1", "b2" }));
			Assert.That(byId[11].Right.Select(b => b.Title).OrderBy(t => t), Is.EqualTo(new[] { "b1", "b2" }),
				"the second left sharing the index key was starved of the shared rights");
		});
	}

	// The other route, and it needs no selector: a collection-backed list index puts one right in
	// several buckets, so two lefts share it under a plain identity join.
	[Test]
	public void JoinMany_CollectionBackedIndex_SharedRightReachesEveryLeft() {
		var tags = new InMemoryDataCache<int, MnAuthor>();
		var books = new InMemoryDataCache<int, MnTaggedBook>();
		var tagIdx = books.CacheCollectionKeyValueListIndex<int>((_, b) => b.TagIds);

		tags.AddOrUpdate(10, new MnAuthor { Id = 10, Name = "fantasy" });
		tags.AddOrUpdate(20, new MnAuthor { Id = 20, Name = "classic" });
		// Book 1 sits in both buckets; book 2 only in tag 20's.
		books.AddOrUpdate(1, new MnTaggedBook { Id = 1, Title = "Shared", TagIds = new List<int> { 10, 20 } });
		books.AddOrUpdate(2, new MnTaggedBook { Id = 2, Title = "Only20", TagIds = new List<int> { 20 } });

		var results = tags.Query().JoinMany(books, tagIdx).Execute();

		var byId = results.ToDictionary(r => r.Left.Id);
		Assert.Multiple(() => {
			Assert.That(byId[10].Right.Select(b => b.Title).OrderBy(t => t), Is.EqualTo(new[] { "Shared" }),
				"tag 10 lost the book it shares with tag 20");
			Assert.That(byId[20].Right.Select(b => b.Title).OrderBy(t => t), Is.EqualTo(new[] { "Only20", "Shared" }));
		});
	}

	[Test]
	public void InnerJoinMany_CollectionBackedIndex_KeepsEveryLeftWithASharedRight() {
		var tags = new InMemoryDataCache<int, MnAuthor>();
		var books = new InMemoryDataCache<int, MnTaggedBook>();
		var tagIdx = books.CacheCollectionKeyValueListIndex<int>((_, b) => b.TagIds);

		tags.AddOrUpdate(10, new MnAuthor { Id = 10, Name = "fantasy" });
		tags.AddOrUpdate(20, new MnAuthor { Id = 20, Name = "classic" });
		books.AddOrUpdate(1, new MnTaggedBook { Id = 1, Title = "Shared", TagIds = new List<int> { 10, 20 } });

		var results = tags.Query().InnerJoinMany(books, tagIdx).Execute();

		Assert.That(results.Count, Is.EqualTo(2), "an inner join dropped a left whose only right is shared");
		Assert.That(results.All(r => r.Right.Count == 1), Is.True);
	}
}
