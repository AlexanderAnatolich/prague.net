namespace Prague.Core.Tests.Join;

using System.Collections.Generic;
using System.Linq;

// ── Probe right-key fixture ─────────────────────────────────────────────────
// A cache key whose GetHashCode reports every call to a test-installed probe. During a JoinMany
// bucket walk the fan-out hashes a right key exactly once per recorded pair and nothing else hashes
// it, so the probe lets a test act at a chosen position of that walk: stand in for the index writer
// (remove / re-add rights under the enumerator) or throw to drive the fan-out's exception path after
// every rent site is live. The probe is thread-local and disarmed by default; a callback that writes
// to a cache must Disarm() first, since the write hashes the key again.

internal readonly struct ProbeKey : IEquatable<ProbeKey> {
	[ThreadStatic] private static Action<int>? _onHash;
	[ThreadStatic] private static int _calls;
	[ThreadStatic] private static int _equalsCalls;

	public readonly int Id;

	public ProbeKey(int id) => Id = id;

	/// <summary>Installs <paramref name="onHash"/>, invoked with the 1-based call number on every hash until <see cref="Disarm"/>.</summary>
	public static void Arm(Action<int> onHash) {
		_calls = 0;
		_onHash = onHash;
	}

	public static void Disarm() => _onHash = null;

	/// <summary>
	/// Equality comparisons on this thread since <see cref="ResetEqualsCalls"/>. A hash-set probe that
	/// meets a stored key with the same hash costs exactly one, so the count exposes how many stored
	/// pairs a walk really inspected — a hash count alone cannot tell a direct slot hit from a linear
	/// scan that hashed once up front.
	/// </summary>
	public static int EqualsCalls => _equalsCalls;

	public static void ResetEqualsCalls() => _equalsCalls = 0;

	/// <summary>Arms a probe that disarms itself and throws on the <paramref name="call"/>-th hash.</summary>
	public static void ThrowOnHashCall(int call) =>
		Arm(n => {
			if (n == call) {
				Disarm();
				throw new InvalidOperationException($"hostile right key hash on call {call}");
			}
		});

	public bool Equals(ProbeKey other) {
		_equalsCalls++;
		return other.Id == Id;
	}

	public override bool Equals(object? obj) => obj is ProbeKey other && Equals(other);

	public override int GetHashCode() {
		var onHash = _onHash;
		if (onHash is not null) {
			onHash(++_calls);
		}

		return Id;
	}

	public override string ToString() => Id.ToString();
}

// PkBook (PK ProbeKey, string Country) — list index on Country; the right side of a LeftSym join.
internal sealed class PkBook : ICacheEquatable<PkBook>, ICacheClonable<PkBook> {
	public int Id { get; init; }
	public string Country { get; init; } = "";
	public string Title { get; init; } = "";

	public bool CacheEquals(PkBook? other) => other is not null && other.Id == Id && other.Country == Country && other.Title == Title;
	public int CacheGetHashCode() => HashCode.Combine(Id, Country, Title);
	public PkBook Clone() => new() { Id = Id, Country = Country, Title = Title };
}

// PkTaggedBook (PK ProbeKey, List<int> TagIds) — owner side of a symmetric collection index.
internal sealed class PkTaggedBook : ICacheEquatable<PkTaggedBook>, ICacheClonable<PkTaggedBook> {
	public int Id { get; init; }
	public List<int> TagIds { get; init; } = new();
	public string Title { get; init; } = "";

	public bool CacheEquals(PkTaggedBook? other) =>
		other is not null && other.Id == Id && other.Title == Title && other.TagIds.SequenceEqual(TagIds);
	public int CacheGetHashCode() => HashCode.Combine(Id, Title);
	public PkTaggedBook Clone() => new() { Id = Id, Title = Title, TagIds = new List<int>(TagIds) };
}
