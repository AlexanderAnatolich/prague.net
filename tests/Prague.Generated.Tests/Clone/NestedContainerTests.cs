namespace Prague.Generated.Tests.Clone;

using NUnit.Framework;
using Prague.Generated.Tests.Models;

/// <summary>
///   Clone, equality and hash of containers whose values or elements are themselves containers
///   (<c>Dictionary&lt;K, List&lt;V&gt;&gt;</c>, <c>List&lt;List&lt;T&gt;&gt;</c>, ...): every level is a copy, and equal
///   content is equal.
/// </summary>
[TestFixture]
public class NestedContainerTests {
	private static NestedContainerEntity Create() => new() {
		Id = 1,
		OptionsByCountry = new() { ["BG"] = ["sofia", "plovdiv"], ["RO"] = ["bucharest"] },
		NullableOptionsByCountry = new() { ["BG"] = ["varna"] },
		ArraysByKey = new() { ["a"] = ["x", "y"] },
		SetsByKey = new() { ["s"] = [1, 2, 3] },
		NestedDictionaries = new() { ["outer"] = new() { ["inner"] = 7 } },
		ItemsByKey = new() { ["k"] = [new NestedContainerItem { Name = "one", Tags = ["t1"] }] },
		ListOfLists = [["a", "b"], ["c"]],
		ListOfDictionaries = [new() { ["k"] = "v" }],
	};

	[Test]
	public void Clone_CopiesEveryLevel() {
		var original = Create();

		var cloned = original.Clone();

		Assert.That(cloned.OptionsByCountry, Is.Not.SameAs(original.OptionsByCountry));
		Assert.That(cloned.OptionsByCountry["BG"], Is.Not.SameAs(original.OptionsByCountry["BG"]));
		Assert.That(cloned.OptionsByCountry["BG"], Is.EqualTo(new[] { "sofia", "plovdiv" }));
		Assert.That(cloned.NullableOptionsByCountry!["BG"], Is.Not.SameAs(original.NullableOptionsByCountry!["BG"]));
		Assert.That(cloned.ArraysByKey["a"], Is.Not.SameAs(original.ArraysByKey["a"]));
		Assert.That(cloned.SetsByKey["s"], Is.Not.SameAs(original.SetsByKey["s"]));
		Assert.That(cloned.NestedDictionaries["outer"], Is.Not.SameAs(original.NestedDictionaries["outer"]));
		Assert.That(cloned.NestedDictionaries["outer"]["inner"], Is.EqualTo(7));
		Assert.That(cloned.ItemsByKey["k"][0], Is.Not.SameAs(original.ItemsByKey["k"][0]));
		Assert.That(cloned.ItemsByKey["k"][0].Tags, Is.Not.SameAs(original.ItemsByKey["k"][0].Tags));
		Assert.That(cloned.ListOfLists[0], Is.Not.SameAs(original.ListOfLists[0]));
		Assert.That(cloned.ListOfDictionaries[0], Is.Not.SameAs(original.ListOfDictionaries[0]));
	}

	[Test]
	public void Clone_MutatingAnInnerContainer_LeavesTheOriginalAlone() {
		var original = Create();
		var cloned = original.Clone();

		cloned.OptionsByCountry["BG"].Add("burgas");
		cloned.NestedDictionaries["outer"]["inner"] = 8;
		cloned.ListOfLists[0].Add("z");

		Assert.That(original.OptionsByCountry["BG"], Has.Count.EqualTo(2));
		Assert.That(original.NestedDictionaries["outer"]["inner"], Is.EqualTo(7));
		Assert.That(original.ListOfLists[0], Has.Count.EqualTo(2));
	}

	[Test]
	public void Clone_NullNullableContainer_StaysNull() {
		var original = Create();
		original.NullableOptionsByCountry = null;

		Assert.That(original.Clone().NullableOptionsByCountry, Is.Null);
	}

	[Test]
	public void CacheEquals_EqualContentInDistinctInstances_IsEqualWithTheSameHash() {
		var left = Create();
		var right = Create();

		Assert.That(left.CacheEquals(right), Is.True);
		Assert.That(left.CacheEquals(right, forceDeep: true), Is.True);
		Assert.That(left.CacheGetHashCode(), Is.EqualTo(right.CacheGetHashCode()));
		Assert.That(left.CacheEquals(left.Clone()), Is.True);
	}

	[Test]
	public void CacheEquals_ADifferenceInAnInnerContainer_IsNotEqual() {
		var left = Create();

		var inList = Create();
		inList.OptionsByCountry["BG"][1] = "varna";
		var inDictionary = Create();
		inDictionary.NestedDictionaries["outer"]["inner"] = 8;
		var inItem = Create();
		inItem.ItemsByKey["k"][0].Tags.Add("t2");
		var inListOfLists = Create();
		inListOfLists.ListOfLists[1].Add("d");
		var inSet = Create();
		inSet.SetsByKey["s"].Add(4);

		Assert.That(left.CacheEquals(inList), Is.False);
		Assert.That(left.CacheEquals(inDictionary), Is.False);
		Assert.That(left.CacheEquals(inItem), Is.False);
		Assert.That(left.CacheEquals(inListOfLists), Is.False);
		Assert.That(left.CacheEquals(inSet), Is.False);
	}
}
