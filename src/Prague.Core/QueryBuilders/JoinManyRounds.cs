namespace Prague.Core;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Prague.Core.Collections;

// ── JoinMany right-key first-fit rounds ───────────────────────────────────
//
// A paired core stores its candidates as ValueSet<JoinedKeyPair<TLeftKey, TRightKey>>
// whose identity is the RIGHT key only, so one pair set cannot carry (L1, r) and
// (L2, r) at the same time. JoinMany resolvers whose lefts share rights (several
// lefts behind one lookup group, a non-injective key selector, overlapping
// collection buckets) therefore spread their pairs over ROUNDS: every (left, right)
// pair goes to the earliest round whose set does not yet contain that right key,
// and the resolver runs one paired execute per round. Right uniqueness per round
// holds by construction — ValueSet.Add returning false IS the membership test.
//
// Round 0 is an inline field sized for the whole join; further rounds are the
// cold path (a right-key collision must happen first) and live in an array
// rented from PragueArrayPool, so the single-round case never touches the heap.

/// <summary>
/// Pair sets for a rounds-based JoinMany execution: first-fits every
/// <see cref="JoinedKeyPair{TJoinedKey,TKey}"/> into the earliest round without its right key
/// and hands each round's set out by reference so the resolver can move it into a paired core.
/// </summary>
/// <typeparam name="TLeftKey">Left cache's key type (the pair's joined key).</typeparam>
/// <typeparam name="TRightKey">Right cache's key type (the pair's identity).</typeparam>
internal ref struct JoinManyRounds<TLeftKey, TRightKey>
	where TLeftKey : notnull
	where TRightKey : notnull, IEquatable<TRightKey> {

	private const int InitialExtraRounds = 4;

	// Round 0 inline; _extra[i] is round i + 1. _count is the number of rounds in use,
	// so _extra holds _count - 1 initialised sets (entries past that are pool garbage).
	private ValueSet<JoinedKeyPair<TLeftKey, TRightKey>, DefaultKeyComparer<JoinedKeyPair<TLeftKey, TRightKey>>> _round0;
	private ValueSet<JoinedKeyPair<TLeftKey, TRightKey>, DefaultKeyComparer<JoinedKeyPair<TLeftKey, TRightKey>>>[]? _extra;
	private int _count;

	/// <param name="expectedCapacity">Pair capacity hint for round 0 (every pair lands there when no right key repeats).</param>
	public JoinManyRounds(int expectedCapacity) {
		_round0 = new ValueSet<JoinedKeyPair<TLeftKey, TRightKey>, DefaultKeyComparer<JoinedKeyPair<TLeftKey, TRightKey>>>(expectedCapacity);
		_extra = null;
		_count = 1;
	}

	/// <summary>Number of rounds in use. A round exists only once a pair landed in it, except round 0.</summary>
	public readonly int Count => _count;

	/// <summary>
	/// The pair set of round <paramref name="index"/>. The resolver reads it to build a paired
	/// core and writes <c>default</c> over it at the ownership hand-off so <see cref="Dispose"/>
	/// does not return the same arrays a second time.
	/// </summary>
	public ref ValueSet<JoinedKeyPair<TLeftKey, TRightKey>, DefaultKeyComparer<JoinedKeyPair<TLeftKey, TRightKey>>> this[int index] {
		[UnscopedRef]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get {
			Debug.Assert((uint)index < (uint)_count, "round index out of range");
			if (index == 0)
				return ref _round0;

			return ref _extra![index - 1];
		}
	}

	/// <summary>
	/// Places <paramref name="pair"/> in the earliest round whose set does not hold its right key and
	/// returns that round. Every pair of a key placed this way lands in the first free round, so the
	/// rounds holding a key always form a prefix [0, k): the search is a binary search over the rounds,
	/// O(log rounds) membership probes with the pair hashed once for all of them. A right shared by m
	/// lefts therefore costs O(m log m) instead of the m²/2 rejected probes of a linear first-fit that
	/// restarts at round 0. Hinted adds on the same instance can break the prefix; the tail loop then
	/// still yields a round without the key, just not necessarily the earliest.
	/// </summary>
	public int Add(JoinedKeyPair<TLeftKey, TRightKey> pair) {
		// Every round shares the comparer, so round 0's hash is every round's hash.
		var hashCode = _round0.HashOf(pair);
		var lo = 0;
		var hi = _count;
		while (lo < hi) {
			var mid = (lo + hi) >> 1;
			if (this[mid].Contains(pair, hashCode)) {
				lo = mid + 1;
			} else {
				hi = mid;
			}
		}

		while (true) {
			if (lo == _count) {
				AddRound();
			}

			if (this[lo].Add(pair, hashCode)) {
				return lo;
			}

			lo++;
		}
	}

	/// <summary>
	/// First-fits <paramref name="pair"/> into the earliest round at or after
	/// <paramref name="startRound"/> whose set does not contain the pair's right key; returns the
	/// round it landed in. Uniqueness per round does not depend on the start: a caller that knows
	/// the pair's right key already sits in the first <paramref name="startRound"/> rounds only
	/// skips probes that would fail anyway.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public int Add(JoinedKeyPair<TLeftKey, TRightKey> pair, int startRound) {
		var round = startRound < _count ? startRound : _count;
		while (true) {
			if (round == _count)
				AddRound();

			if (this[round].Add(pair))
				return round;

			round++;
		}
	}

	/// <summary>
	/// Cold path: a right key collided in every existing round. Appends an empty round,
	/// renting (or growing by rent-copy-return) the extra-rounds array.
	/// </summary>
	[MethodImpl(MethodImplOptions.NoInlining)]
	private void AddRound() {
		var pool = PragueArrayPool<ValueSet<JoinedKeyPair<TLeftKey, TRightKey>, DefaultKeyComparer<JoinedKeyPair<TLeftKey, TRightKey>>>>.Pool;
		var extra = _extra;
		var used = _count - 1;
		if (extra is null) {
			extra = pool.Rent(InitialExtraRounds);
		} else if (used == extra.Length) {
			var grown = pool.Rent(extra.Length * 2);
			Array.Copy(extra, grown, used);
			// The old array still holds copies of the moved sets; clearing on return keeps them
			// from ever being disposed through that array.
			pool.Return(extra, clearArray: true);
			extra = grown;
		}

		// Extra rounds carry the collision overflow only — inline capacity, no rent until they grow.
		extra[used] = new ValueSet<JoinedKeyPair<TLeftKey, TRightKey>, DefaultKeyComparer<JoinedKeyPair<TLeftKey, TRightKey>>>();
		_extra = extra;
		_count = used + 2;
	}

	/// <summary>
	/// Disposes every round still owned (a round handed to a paired core was overwritten with
	/// <c>default</c> by the resolver and is skipped) and returns the extra-rounds array.
	/// </summary>
	public void Dispose() {
		if (_round0.IsInitlized)
			_round0.Dispose();

		_round0 = default;

		var extra = _extra;
		if (extra is not null) {
			var used = _count - 1;
			for (var i = 0; i < used; i++) {
				ref var round = ref extra[i];
				if (round.IsInitlized)
					round.Dispose();
			}
			PragueArrayPool<ValueSet<JoinedKeyPair<TLeftKey, TRightKey>, DefaultKeyComparer<JoinedKeyPair<TLeftKey, TRightKey>>>>.Pool.Return(extra, clearArray: true);
			_extra = null;
		}

		_count = 0;
	}
}
