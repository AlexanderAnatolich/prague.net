namespace Prague.Core.Tests.Join;

using System.Linq;
using Prague.Core;
using NUnit.Framework;

// Tie order is one contract across every sorted terminal: rows that compare equal come out in
// encounter order whether the query is unbounded (classic stable sort), a finite page that is most of
// the result (routed to the same classic sort by TopKPlan), a smaller page over a big share of the
// result (collect plan) or a sequence of small pages (heap plan). Before the classic sort was stable,
// Execute() and Execute(0, N) could disagree on equal rows.
[TestFixture]
public class SortedTieOrderCoreTests {
	internal sealed class StItem : ICacheEquatable<StItem>, ICacheClonable<StItem> {
		public int Id { get; init; }
		public int Group { get; init; }
		public bool CacheEquals(StItem? other) => other is not null && Id == other.Id && Group == other.Group;
		public int CacheGetHashCode() => HashCode.Combine(Id, Group);
		public StItem Clone() => new() { Id = Id, Group = Group };
	}

	private const int N = 96;
	private InMemoryDataCache<int, StItem> _left = null!;
	private InMemoryDataCache<int, StItem> _right = null!;

	[OneTimeSetUp]
	public void SetUp() {
		_left = new InMemoryDataCache<int, StItem>();
		_right = new InMemoryDataCache<int, StItem>();
		for (var i = 0; i < N; i++) {
			// Five groups: plenty of ties inside each group, distinct groups in between.
			_left.AddOrUpdate(i, new StItem { Id = i, Group = i % 5 });
			_right.AddOrUpdate(i, new StItem { Id = i, Group = 0 });
		}
	}

	private static IComparer<StItem> AllEqual => Comparer<StItem>.Create(static (_, _) => 0);
	private static IComparer<StItem> ByGroup => Comparer<StItem>.Create(static (a, b) => a.Group.CompareTo(b.Group));

	private static int[] Pages(Func<int, int, int[]> page, int pageSize) {
		var ids = new List<int>();
		for (var skip = 0; skip < N; skip += pageSize) {
			ids.AddRange(page(skip, pageSize));
		}

		return ids.ToArray();
	}

	[Test]
	public void Simple_AllEqual_UnboundedIsEncounterOrder() {
		using var encounter = _left.Query().ExecutePooled();
		using var sorted = _left.Query().Sort(AllEqual).ExecutePooled();

		Assert.That(sorted.Select(x => x.Id), Is.EqualTo(encounter.Select(x => x.Id)));
	}

	[TestCase(2)]
	[TestCase(17)]
	[TestCase(32)]
	public void Simple_Unbounded_FullPage_AndPages_Agree(int pageSize) {
		foreach (var comparer in new[] { AllEqual, ByGroup }) {
			using var unbounded = _left.Query().Sort(comparer).ExecutePooled();
			using var fullPage = _left.Query().Sort(comparer).ExecutePooled(0, N);
			var expected = unbounded.Select(x => x.Id).ToArray();

			Assert.That(fullPage.Select(x => x.Id), Is.EqualTo(expected), "one finite page over everything");
			Assert.That(Pages((skip, take) => {
				using var page = _left.Query().Sort(comparer).ExecutePooled(skip, take);
				return page.Select(x => x.Id).ToArray();
			}, pageSize), Is.EqualTo(expected), $"pages of {pageSize}");
		}
	}

	[TestCase(2)]
	[TestCase(17)]
	[TestCase(32)]
	public void InnerJoin_Unbounded_FullPage_AndPages_Agree(int pageSize) {
		foreach (var comparer in new[] { AllEqual, ByGroup }) {
			using var unbounded = _left.Query().Sort(comparer).InnerJoinOne(_right).ExecutePooled();
			using var fullPage = _left.Query().Sort(comparer).InnerJoinOne(_right).ExecutePooled(0, N);
			var expected = unbounded.Select(x => x.Left.Id).ToArray();

			Assert.That(expected, Has.Length.EqualTo(N));
			Assert.That(fullPage.Select(x => x.Left.Id), Is.EqualTo(expected), "one finite page over everything");
			Assert.That(Pages((skip, take) => {
				using var page = _left.Query().Sort(comparer).InnerJoinOne(_right).ExecutePooled(skip, take);
				return page.Select(x => x.Left.Id).ToArray();
			}, pageSize), Is.EqualTo(expected), $"pages of {pageSize}");
		}
	}

	// Pages on both sides of TopKPlan's share (3/5 of 96 rows = 57.6): a page holding 58 rows or more
	// — (0, 58), (0, 96), (24, 72), (30, 66) — runs the classic sort; (0, 57), a deep page such as
	// (90, 96) that holds only 6 rows, and the small pages run the top-K plans. Every page must be the
	// matching slice of the unbounded order.
	[TestCase(0, 96)]
	[TestCase(0, 58)]
	[TestCase(0, 57)]
	[TestCase(24, 72)]
	[TestCase(30, 66)]
	[TestCase(50, 50)]
	[TestCase(70, 10)]
	[TestCase(40, 20)]
	[TestCase(90, 96)]
	public void Simple_Page_IsTheSliceOfTheUnboundedOrder(int skip, int take) {
		foreach (var comparer in new[] { AllEqual, ByGroup }) {
			using var unbounded = _left.Query().Sort(comparer).ExecutePooled();
			using var page = _left.Query().Sort(comparer).ExecutePooled(skip, take);

			Assert.That(page.Select(x => x.Id), Is.EqualTo(unbounded.Select(x => x.Id).Skip(skip).Take(take)));
		}
	}

	// The joined path goes classic only for the page that holds every row, (0, 96); the others keep
	// the top-K plan and its per-page join fill.
	[TestCase(0, 96)]
	[TestCase(0, 95)]
	[TestCase(0, 58)]
	[TestCase(24, 72)]
	[TestCase(70, 10)]
	[TestCase(90, 96)]
	public void InnerJoin_Page_IsTheSliceOfTheUnboundedOrder(int skip, int take) {
		foreach (var comparer in new[] { AllEqual, ByGroup }) {
			using var unbounded = _left.Query().Sort(comparer).InnerJoinOne(_right).ExecutePooled();
			using var page = _left.Query().Sort(comparer).InnerJoinOne(_right).ExecutePooled(skip, take);

			Assert.That(page.Select(x => x.Left.Id), Is.EqualTo(unbounded.Select(x => x.Left.Id).Skip(skip).Take(take)));
		}
	}

	[Test]
	public void ByGroup_Unbounded_IsSortedAndStableWithinGroups() {
		using var encounter = _left.Query().ExecutePooled();
		using var sorted = _left.Query().Sort(ByGroup).ExecutePooled();

		var expected = encounter.OrderBy(x => x.Group).Select(x => x.Id).ToArray();
		Assert.That(sorted.Select(x => x.Id), Is.EqualTo(expected), "LINQ OrderBy is the stable reference");
	}
}
