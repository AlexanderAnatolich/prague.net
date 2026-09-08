namespace Prague.Core.Tests.Join;

using Prague.Core;
using Prague.Core.Collections;
using NUnit.Framework;

// JoinedKeyPair is the element type of every paired core and of the JoinMany rounds: its identity is
// the right key alone, and a pair set probes it on every hash hit. The comparison must therefore stay
// allocation-free for value-type keys — a boxed int per probe turns a rounds walk over a shared right
// into hundreds of megabytes.
[TestFixture]
public class JoinedKeyPairTests {
	[Test]
	public void Equals_IdentityIsTheRightKey_JoinedKeyIgnored() {
		var a = new JoinedKeyPair<int, int>(1, 100);
		var b = new JoinedKeyPair<int, int>(2, 100);
		var c = new JoinedKeyPair<int, int>(1, 200);

		Assert.That(a.Equals(b), Is.True);
		Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
		Assert.That(a.Equals(c), Is.False);
	}

	[Test]
	public void Equals_ValueTypeKey_DoesNotBox() {
		var set = new ValueSet<JoinedKeyPair<int, int>, DefaultKeyComparer<JoinedKeyPair<int, int>>>(4);
		try {
			Assert.That(set.Add(new JoinedKeyPair<int, int>(1, 100)), Is.True);
			var colliding = new JoinedKeyPair<int, int>(2, 100);

			// Every Add below hits the stored pair's hash and reaches the comparer: the exact production path.
			for (var i = 0; i < 1_000; i++) {
				set.Add(colliding);
			}

			var rejected = 0;
			var before = GC.GetAllocatedBytesForCurrentThread();
			for (var i = 0; i < 100_000; i++) {
				if (!set.Add(colliding)) {
					rejected++;
				}
			}

			var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
			Assert.That(rejected, Is.EqualTo(100_000));
			Assert.That(allocated, Is.LessThan(1024), "a rejected probe must not allocate (a boxed key costs 24 bytes each)");
		} finally {
			set.Dispose();
		}
	}
}
