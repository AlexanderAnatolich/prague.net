namespace Prague.Generated.Tests.Kafka;

using NUnit.Framework;
using Prague.Generated.Tests.TestEntities;
using Prague.Kafka;

/// <summary>
///   <c>AddOrUpdateAndProduce(document, force: true)</c> keeps the conditional local write and makes only
///   the produce step unconditional. Broker-free: no producer is configured, so reaching the produce step
///   is observable as the "Producer not configured" throw, and the local state shows the write was
///   still conditional.
/// </summary>
[TestFixture]
public class AddOrUpdateAndProduceForceTests {
	[Test]
	public void Unforced_EqualDocument_NeitherSwapsNorProduces() {
		var cache = new TestEntityWithoutTimestampCache();
		var first = new TestEntityWithoutTimestamp { Id = 1, Name = "a" };
		var second = new TestEntityWithoutTimestamp { Id = 1, Name = "a" };
		cache.AddOrUpdate(first);

		Assert.DoesNotThrow(() => cache.AddOrUpdateAndProduce(second), "no change, so the missing producer is never reached");
		Assert.DoesNotThrow(() => cache.AddOrUpdateAndProduce(second, force: false), "force: false is the plain form");
		Assert.That(cache.TryGet(1, out var kept) && ReferenceEquals(kept, first), "the resident instance stays");
	}

	[Test]
	public void Forced_EqualDocument_ProducesWithoutSwapping() {
		var cache = new TestEntityWithoutTimestampCache();
		var first = new TestEntityWithoutTimestamp { Id = 1, Name = "a" };
		var second = new TestEntityWithoutTimestamp { Id = 1, Name = "a" };
		cache.AddOrUpdate(first);

		var ex = Assert.Throws<InvalidOperationException>(() => cache.AddOrUpdateAndProduce(second, force: true));
		Assert.That(ex!.Message, Does.Contain("Producer not configured"), "the produce step was reached although nothing changed");
		Assert.That(cache.TryGet(1, out var kept) && ReferenceEquals(kept, first), "the local write is still conditional: no swap for an equal document");
	}

	[Test]
	public void Forced_ChangedDocument_WritesLocallyThenProduces() {
		var cache = new TestEntityWithoutTimestampCache();
		cache.AddOrUpdate(new TestEntityWithoutTimestamp { Id = 1, Name = "a" });
		var changed = new TestEntityWithoutTimestamp { Id = 1, Name = "b" };

		Assert.Throws<InvalidOperationException>(() => cache.AddOrUpdateAndProduce(changed, force: true));
		Assert.That(cache.TryGet(1, out var resident) && ReferenceEquals(resident, changed), "the local write landed before the produce step");
	}
}

/// <summary>
///   <c>ForceProduceAll()</c> is a repair operation and refuses to run without a producer, before taking
///   the store snapshot — an unconfigured cache fails fast instead of silently producing nothing.
/// </summary>
[TestFixture]
public class ForceProduceAllTests {
	[Test]
	public void NoProducer_Throws_EvenForAnEmptyCache() {
		var cache = new TestEntityWithoutTimestampCache();
		var ex = Assert.Throws<InvalidOperationException>(() => cache.ForceProduceAll());
		Assert.That(ex!.Message, Does.Contain("Producer not configured"));
	}

	[Test]
	public void NoProducer_LeavesResidentRecordsUntouched() {
		var cache = new TestEntityWithoutTimestampCache();
		var doc = new TestEntityWithoutTimestamp { Id = 1, Name = "a" };
		cache.AddOrUpdate(doc);

		Assert.Throws<InvalidOperationException>(() => cache.ForceProduceAll());
		Assert.That(cache.TryGet(1, out var kept) && ReferenceEquals(kept, doc), "produce-all never writes locally");
	}
}
