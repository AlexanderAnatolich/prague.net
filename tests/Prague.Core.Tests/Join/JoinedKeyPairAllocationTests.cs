namespace Prague.Core.Tests.Join;

using Prague.Core;
using NUnit.Framework;

/// <summary>
///   <see cref="JoinedKeyPair{TJoinedKey,TKey}"/> is the element type of the pair set every JoinMany
///   builds, so its equality runs on the hot path of every such query. TKey is constrained to
///   notnull only, which makes a direct <c>Key.Equals(other.Key)</c> bind to
///   <see cref="object.Equals(object)"/> and box the key on every comparison — 24 B per call for an
///   int key. The comparison has to stay allocation-free.
/// </summary>
[TestFixture]
// Reads a thread-wide allocation counter.
[NonParallelizable]
public class JoinedKeyPairAllocationTests {
	// Amortised over many calls so runner dust cannot fail it, the way TopK's probes are.
	private const int Iterations = 1_000_000;

	[Test]
	public void Equals_OnAValueTypeKey_DoesNotAllocate() {
		var a = new JoinedKeyPair<int, int>(1, 42);
		var equalKey = new JoinedKeyPair<int, int>(2, 42);
		var otherKey = new JoinedKeyPair<int, int>(2, 43);

		// Warm the intrinsic and the test's own machinery before measuring.
		for (var i = 0; i < 1_000; i++)
			_ = a.Equals(equalKey) & a.Equals(otherKey);

		var before = GC.GetAllocatedBytesForCurrentThread();
		var matches = 0;
		for (var i = 0; i < Iterations; i++)
			if (a.Equals(equalKey)) matches++;
		var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.That(matches, Is.EqualTo(Iterations), "equality is on the right key, so these pairs are equal");
		Assert.That(bytes, Is.LessThan(Iterations / 100),
			$"JoinedKeyPair.Equals allocated {(double)bytes / Iterations:F2} B per call — the key is being boxed");
	}

	// A string key has no value to box; this pins that the fix did not cost the reference case anything
	// and that equality still reads the right key alone.
	[Test]
	public void Equals_ComparesTheRightKeyOnly() {
		var left = new JoinedKeyPair<int, string>(1, "same");
		var right = new JoinedKeyPair<int, string>(999, "same");
		var different = new JoinedKeyPair<int, string>(1, "other");

		Assert.Multiple(() => {
			Assert.That(left.Equals(right), Is.True, "the joined key must not participate in equality");
			Assert.That(left.Equals(different), Is.False);
			Assert.That(left.GetHashCode(), Is.EqualTo(right.GetHashCode()));
		});
	}
}
