namespace Prague.Core.Tests.Join;

using System.Linq;
using Prague.Core;
using NUnit.Framework;

// UseIndex + Where + Sort + InnerJoinOne. The classic joined pipeline runs the inner join first and gives
// every candidate with a matching right a result slot BEFORE the base walk applies the Where predicate;
// rows the predicate rejects must not survive as slots with an empty Left — whichever plan runs the
// query: the unbounded classic sort, a finite page that holds the whole result (routed to the classic
// sort by TopKPlan) or a smaller top-K page. Before the fix a page of exactly the candidate count came
// back with the rejected rows as Left = null, and the ordinary comparer threw on them.
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
