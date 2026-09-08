namespace Prague.Core.Tests.Join;

using System.Linq;
using Prague.Core;
using NUnit.Framework;

// UseIndex + Where + Sort + InnerJoinOne. The classic joined pipeline runs the inner join first and gives
// every candidate with a matching right a result slot BEFORE the base walk applies the Where predicate;
// rows the predicate rejects must not survive as slots with an empty Left — whichever plan runs the
// query: the unbounded classic sort, a finite page that holds the whole result (routed to the classic
// sort by TopKPlan) or a smaller top-K page. Before the fix a page of exactly the candidate count came
// back with the rejected rows as Left = null, and the ordinary comparer threw on them. Cache values may
// be structs, where an unfilled slot and a real default(T) row look the same: the container must
// remember which slots the walk filled instead of reading the value.
[TestFixture]
public class SortedInnerJoinWithFilterCoreTests {
	private InMemoryDataCache<int, SnAuthor> _authors = null!;
	private InMemoryDataCache<int, SnProfile> _profiles = null!;
	private CacheKeyValueListIndex<int, SnAuthor, string> _authorNameIdx = null!;

	[SetUp]
	public void SetUp() {
		_authors = new InMemoryDataCache<int, SnAuthor>();
		_profiles = new InMemoryDataCache<int, SnProfile>();
		_authorNameIdx = _authors.CacheKeyValueListIndex<string>((_, v) => v.Name);
		for (var i = 1; i <= 10; i++) {
			// Every author sits in the one index bucket the query narrows to; every author has a profile,
			// so only the predicate decides who is in the result: the five odd ids.
			_authors.AddOrUpdate(i, new SnAuthor { Id = i, Name = "A" });
			_profiles.AddOrUpdate(i, new SnProfile { Id = i, Bio = $"Bio {i}" });
		}
	}

	private static readonly int[] OddIdsDesc = { 9, 7, 5, 3, 1 };

	[TestCase(int.MaxValue)] // unbounded: the classic sort
	[TestCase(10)]           // the candidate count: a full page, routed to the classic sort
	[TestCase(9)]            // just below: the top-K collect plan
	[TestCase(2)]            // the top-K heap plan
	public void InnerJoinOne_AfterWhere_Pooled_HoldsOnlyRowsPassingThePredicate(int take) {
		using var page = take == int.MaxValue
			? _authors.Query().UseIndex(_authorNameIdx, "A").Where(a => a.Id % 2 == 1).Sort(new AuthorByIdDesc()).InnerJoinOne(_profiles).ExecutePooled()
			: _authors.Query().UseIndex(_authorNameIdx, "A").Where(a => a.Id % 2 == 1).Sort(new AuthorByIdDesc()).InnerJoinOne(_profiles).ExecutePooled(0, take);

		Assert.That(page.Select(r => r.Left).ToArray(), Has.All.Not.Null, "a row the predicate rejected surfaced with an empty Left");
		Assert.That(page.Select(r => r.Left.Id), Is.EqualTo(OddIdsDesc.Take(take)));
		Assert.That(page.TotalCount, Is.EqualTo(5), "TotalCount counts the rows that passed the predicate");
		Assert.That(page.All(r => r.Right is not null && r.Right.Id == r.Left.Id), Is.True);
	}

	[TestCase(int.MaxValue)]
	[TestCase(10)]
	[TestCase(9)]
	public void InnerJoinOne_AfterWhere_Allocating_HoldsOnlyRowsPassingThePredicate(int take) {
		var page = take == int.MaxValue
			? _authors.Query().UseIndex(_authorNameIdx, "A").Where(a => a.Id % 2 == 1).Sort(new AuthorByIdDesc()).InnerJoinOne(_profiles).Execute()
			: _authors.Query().UseIndex(_authorNameIdx, "A").Where(a => a.Id % 2 == 1).Sort(new AuthorByIdDesc()).InnerJoinOne(_profiles).Execute(0, take);

		Assert.That(page.Select(r => r.Left).ToArray(), Has.All.Not.Null);
		Assert.That(page.Select(r => r.Left.Id), Is.EqualTo(OddIdsDesc.Take(take)));
		Assert.That(page.TotalCount, Is.EqualTo(5));
	}

	// Struct cache values: id 0 is default(SvAuthor) and a real row that passes the predicate, so it
	// must stay while the rejected ids 1 and 3 go.
	private static readonly int[] EvenIdsDesc = { 4, 2, 0 };

	[TestCase(int.MaxValue)] // unbounded: the classic sort
	[TestCase(5)]            // the candidate count: a full page, routed to the classic sort
	[TestCase(4)]            // top-K
	public void InnerJoinOne_AfterWhere_StructValues_KeepsTheDefaultRowAndDropsTheRejected(int take) {
		var authors = new InMemoryDataCache<int, SvAuthor>();
		var profiles = new InMemoryDataCache<int, SnProfile>();
		var halfIdx = authors.CacheKeyValueListIndex<int>((_, v) => v.Id / 5);
		for (var i = 0; i < 10; i++) {
			authors.AddOrUpdate(i, new SvAuthor { Id = i });
			profiles.AddOrUpdate(i, new SnProfile { Id = i, Bio = $"Bio {i}" });
		}

		using var page = take == int.MaxValue
			? authors.Query().UseIndex(halfIdx, 0).Where(a => a.Id % 2 == 0).Sort(new SvByIdDesc()).InnerJoinOne(profiles).ExecutePooled()
			: authors.Query().UseIndex(halfIdx, 0).Where(a => a.Id % 2 == 0).Sort(new SvByIdDesc()).InnerJoinOne(profiles).ExecutePooled(0, take);

		Assert.That(page.Select(r => r.Left.Id), Is.EqualTo(EvenIdsDesc.Take(take)), "ids 1 and 3 failed the predicate; id 0 is default(SvAuthor) and real");
		Assert.That(page.Count, Is.EqualTo(Math.Min(take, 3)));
		Assert.That(page.TotalCount, Is.EqualTo(3));
		Assert.That(page.All(r => r.Right is not null && r.Right.Id == r.Left.Id), Is.True);
	}

	[TestCase(int.MaxValue)]
	[TestCase(5)]
	public void InnerJoinOne_AfterWhere_StructValues_Unsorted_KeepsTheDefaultRow(int take) {
		var authors = new InMemoryDataCache<int, SvAuthor>();
		var profiles = new InMemoryDataCache<int, SnProfile>();
		var halfIdx = authors.CacheKeyValueListIndex<int>((_, v) => v.Id / 5);
		for (var i = 0; i < 10; i++) {
			authors.AddOrUpdate(i, new SvAuthor { Id = i });
			profiles.AddOrUpdate(i, new SnProfile { Id = i, Bio = $"Bio {i}" });
		}

		using var page = take == int.MaxValue
			? authors.Query().UseIndex(halfIdx, 0).Where(a => a.Id % 2 == 0).InnerJoinOne(profiles).ExecutePooled()
			: authors.Query().UseIndex(halfIdx, 0).Where(a => a.Id % 2 == 0).InnerJoinOne(profiles).ExecutePooled(0, take);

		Assert.That(page.Select(r => r.Left.Id).OrderBy(i => i), Is.EqualTo(new[] { 0, 2, 4 }));
		Assert.That(page.TotalCount, Is.EqualTo(3));
	}

	// The same shape without the sort: the classic joined pipeline with a skip/take crop.
	[TestCase(int.MaxValue)]
	[TestCase(10)]
	[TestCase(3)]
	public void InnerJoinOne_AfterWhere_Unsorted_HoldsOnlyRowsPassingThePredicate(int take) {
		using var page = take == int.MaxValue
			? _authors.Query().UseIndex(_authorNameIdx, "A").Where(a => a.Id % 2 == 1).InnerJoinOne(_profiles).ExecutePooled()
			: _authors.Query().UseIndex(_authorNameIdx, "A").Where(a => a.Id % 2 == 1).InnerJoinOne(_profiles).ExecutePooled(0, take);

		Assert.That(page.Select(r => r.Left).ToArray(), Has.All.Not.Null);
		Assert.That(page.Select(r => r.Left.Id).ToArray(), Is.All.Matches<int>(static id => id % 2 == 1) & Is.Unique, "only rows the predicate accepted, each once");
		Assert.That(page.Count, Is.EqualTo(Math.Min(take, 5)));
		Assert.That(page.TotalCount, Is.EqualTo(5));
	}
}

internal readonly struct SvAuthor : ICacheEquatable<SvAuthor>, ICacheClonable<SvAuthor> {
	public int Id { get; init; }
	public bool CacheEquals(SvAuthor other) => other.Id == Id;
	public int CacheGetHashCode() => Id;
	public SvAuthor Clone() => this;
}

internal sealed class SvByIdDesc : IComparer<SvAuthor> {
	public int Compare(SvAuthor x, SvAuthor y) => y.Id.CompareTo(x.Id);
}
