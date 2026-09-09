namespace Prague.Core;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Prague.Core.Collections;

// ── JoinMany fan-out: one pair set, right → extra-lefts chains ─────────────
//
// A paired core stores its candidates as ValueSet<JoinedKeyPair<TLeftKey, TRightKey>> whose identity
// is the RIGHT key only, so one set cannot carry (L1, r) and (L2, r) at the same time. Every JoinMany
// resolver therefore keeps ONE pair per distinct right in the set — its JoinedKey is the first left
// that recorded the right, which is enough for the user's filter (UseIndex / Where / Or all narrow by
// right key) and for one paired execute — and records on the side which OTHER lefts each right belongs
// to: a chain of left keys per pair slot, in pooled arrays. Delivery runs the keyed paired execute once:
// the store hands the container (first left, right key, value) per surviving right, the container adds
// the value to the first left and then to every left in the right's chain.
//
// The chains are lazy. While no right is recorded by a second left — the ordinary FK join, where every
// right belongs to exactly one left — nothing but the pair itself is written and no chain array is
// rented, so the fan-out costs what a plain pair set does; the resolver then skips the delivery as
// well and runs the plain paired execute (see SingleLeftPerRight). The first shared right rents the
// chain arrays and gives every slot recorded so far an empty chain.
//
// Pair slots are stable while nothing is removed (the recording phase only adds) and survive the set's
// growth, and the user filter can only remove pairs — in place — so a slot recorded here still names
// the same right when the surviving pairs are delivered.
//
// A right the bucket enumerator yields twice for the same left (removed and re-added under the walk)
// is recorded once: the walk is per left, so the most recent recorder of that right — the pair's
// first left, or the head of its chain — is the current left exactly when the sighting is a repeat.
// Slot capacities therefore equal the adds a slot can receive.

/// <summary>
/// Pair set plus right → extra-lefts chains for a JoinMany execution: the resolver records every
/// (left, right) pair, hands the set to one paired core and delivers every surviving right to all of
/// its lefts — straight from the store when no right is shared, through
/// <see cref="Delivery{TRightValue,TContainer}"/> otherwise.
/// </summary>
/// <typeparam name="TLeftKey">Left cache's key type (the pair's joined key).</typeparam>
/// <typeparam name="TRightKey">Right cache's key type (the pair's identity).</typeparam>
internal ref struct JoinManyFanOut<TLeftKey, TRightKey>
	where TLeftKey : notnull
	where TRightKey : notnull, IEquatable<TRightKey> {

	internal const int NoChain = -1;
	private const int MinCapacity = 16;

	private ValueSet<JoinedKeyPair<TLeftKey, TRightKey>, DefaultKeyComparer<JoinedKeyPair<TLeftKey, TRightKey>>> _pairs;
	// The set's Count at the hand-off, when the set itself is gone from here; before that the set is
	// asked directly, so recording keeps no counter of its own.
	private int _distinctRightsHandedOff;
	private bool _handedOff;
	// Rented by the first shared right. _heads[slot]: first chain node of the pair stored at that slot,
	// or NoChain; a node carries a left recorded for the right AFTER its first one, and the next node.
	private int[]? _heads;
	private TLeftKey[]? _nodeLeft;
	private int[]? _nodeNext;
	private int _nodeCount;

	/// <param name="expectedPairs">Capacity hint for the pair set.</param>
	public JoinManyFanOut(int expectedPairs) {
		_pairs = new ValueSet<JoinedKeyPair<TLeftKey, TRightKey>, DefaultKeyComparer<JoinedKeyPair<TLeftKey, TRightKey>>>(Math.Max(expectedPairs, MinCapacity));
		_distinctRightsHandedOff = 0;
		_handedOff = false;
		_heads = null;
		_nodeLeft = null;
		_nodeNext = null;
		_nodeCount = 0;
	}

	/// <summary>Distinct rights recorded: the pair set's size (remembered across the hand-off).</summary>
	public readonly int DistinctRights => _handedOff ? _distinctRightsHandedOff : _pairs.Count;

	/// <summary>(left, right) pairs recorded so far, repeat sightings excluded.</summary>
	public readonly int PairCount => DistinctRights + _nodeCount;

	/// <summary>
	/// True while every recorded right belongs to exactly one left, so a pair's JoinedKey is its whole
	/// chain: no chain array has been rented and the resolver runs the plain paired execute instead of
	/// a <see cref="Delivery{TRightValue,TContainer}"/>.
	/// </summary>
	public readonly bool SingleLeftPerRight => _nodeCount == 0;

	/// <summary>Whether the chain arrays exist — rented by the first right a second left recorded.</summary>
	internal readonly bool HasChains => _heads is not null;

	/// <summary>
	/// The pair set — one pair per distinct right, its JoinedKey the first left that recorded it. The
	/// resolver builds the paired core over a copy and then calls <see cref="MarkPairsHandedOff"/>.
	/// </summary>
	public ref ValueSet<JoinedKeyPair<TLeftKey, TRightKey>, DefaultKeyComparer<JoinedKeyPair<TLeftKey, TRightKey>>> Pairs {
		[UnscopedRef]
		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		get => ref _pairs;
	}

	/// <summary>
	/// Records (left, right) for every right in <paramref name="bucket"/> — one enumeration, the
	/// bucket's own hash-free walk — and returns how many were recorded, repeat sightings excluded:
	/// the exact capacity the left's slot must reserve.
	/// </summary>
	public int RecordBucket(TLeftKey left, PooledSet<TRightKey, DefaultKeyComparer<TRightKey>> bucket) {
		var added = 0;
		foreach (var right in bucket) {
			if (Record(left, right)) {
				added++;
			}
		}

		return added;
	}

	/// <summary>
	/// Records (left, right). False when the same left already recorded this right — the bucket
	/// enumerator yielded it twice — so the caller leaves it out of the slot capacity.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	public bool Record(TLeftKey left, TRightKey right) {
		var added = _pairs.AddOrFind(new JoinedKeyPair<TLeftKey, TRightKey>(left, right), out var slot);
		if (added) {
			if (_heads is not null) {
				// Chains exist, so the new slot needs an empty one. Nothing is removed while recording:
				// new rights take slots 0, 1, 2, … in order.
				if (slot >= _heads.Length) {
					GrowHeads();
				}

				_heads[slot] = NoChain;
			}

			return true;
		}

		return RecordShared(left, slot);
	}

	// The right already belongs to a left: cold next to the FK shape, where every right is new. A repeat
	// sighting for the same left records nothing; another left joins the right's chain.
	[MethodImpl(MethodImplOptions.NoInlining)]
	private bool RecordShared(TLeftKey left, int slot) {
		var head = _heads is null ? NoChain : _heads[slot];
		var latest = head == NoChain ? _pairs.ValueAt(slot).JoinedKey : _nodeLeft![head];
		if (EqualityComparer<TLeftKey>.Default.Equals(latest, left)) {
			return false;
		}

		if (_heads is null) {
			RentChains();
		} else if (_nodeCount == _nodeLeft!.Length) {
			GrowNodes();
		}

		var node = _nodeCount;
		_nodeLeft![node] = left;
		_nodeNext![node] = head;
		_heads![slot] = node;
		_nodeCount = node + 1;
		return true;
	}

	/// <summary>
	/// The pair set now belongs to the paired core that received a copy of it (the core disposes it):
	/// <see cref="Dispose"/> leaves the pairs alone from here on and only returns the chains.
	/// </summary>
	public void MarkPairsHandedOff() {
		_distinctRightsHandedOff = _pairs.Count;
		_handedOff = true;
		_pairs = default;
	}

	// Chain storage for a Delivery, valid once some right is shared (!SingleLeftPerRight); the arrays
	// stay owned (and returned) by this instance.
	internal readonly int[] Heads => _heads!;
	internal readonly TLeftKey[] NodeLefts => _nodeLeft!;
	internal readonly int[] NodeNexts => _nodeNext!;

	public void Dispose() {
		if (_pairs.IsInitlized) {
			_pairs.Dispose();
		}

		_pairs = default;
		if (_heads is not null) {
			PragueArrayPool<int>.Pool.Return(_heads);
			_heads = null;
		}

		if (_nodeLeft is not null) {
			PragueArrayPool<TLeftKey>.Pool.Return(_nodeLeft, RuntimeHelpers.IsReferenceOrContainsReferences<TLeftKey>());
			_nodeLeft = null;
		}

		if (_nodeNext is not null) {
			PragueArrayPool<int>.Pool.Return(_nodeNext);
			_nodeNext = null;
		}

		_nodeCount = 0;
		_distinctRightsHandedOff = 0;
		_handedOff = false;
	}

	// First shared right: rent the chains and give every slot recorded so far an empty one. Slots that
	// arrive later get theirs in Record.
	[MethodImpl(MethodImplOptions.NoInlining)]
	private void RentChains() {
		var recorded = _pairs.Count;
		var heads = PragueArrayPool<int>.Pool.Rent(Math.Max(recorded, MinCapacity));
		heads.AsSpan(0, recorded).Fill(NoChain);
		_heads = heads;
		_nodeLeft = PragueArrayPool<TLeftKey>.Pool.Rent(MinCapacity);
		_nodeNext = PragueArrayPool<int>.Pool.Rent(MinCapacity);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private void GrowHeads() {
		var heads = _heads!;
		var grown = PragueArrayPool<int>.Pool.Rent(heads.Length * 2);
		Array.Copy(heads, grown, heads.Length);
		PragueArrayPool<int>.Pool.Return(heads);
		_heads = grown;
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private void GrowNodes() {
		var nodeLeft = _nodeLeft!;
		var nodeNext = _nodeNext!;
		var length = nodeLeft.Length;
		var lefts = PragueArrayPool<TLeftKey>.Pool.Rent(length * 2);
		var nexts = PragueArrayPool<int>.Pool.Rent(length * 2);
		Array.Copy(nodeLeft, lefts, length);
		Array.Copy(nodeNext, nexts, length);
		PragueArrayPool<TLeftKey>.Pool.Return(nodeLeft, RuntimeHelpers.IsReferenceOrContainsReferences<TLeftKey>());
		PragueArrayPool<int>.Pool.Return(nodeNext);
		_nodeLeft = lefts;
		_nodeNext = nexts;
	}

	/// <summary>
	/// Container for the keyed paired execute: receives one surviving right at a time and adds its
	/// value to the right's first left and to every left in its chain. Holds the target container by
	/// value — a ref field cannot refer to a ref struct — so the caller copies <see cref="Inner"/> back
	/// once the execute returns. The pair set is a by-value snapshot taken after the user filter
	/// narrowed it: it shares the set's rented arrays (or, for an inline-stored set, copies its slots),
	/// which is all a slot lookup needs, and it is never disposed — the paired core owns the real set.
	/// A resolver builds one only when some right is shared (see <see cref="SingleLeftPerRight"/>), so
	/// the chain arrays exist.
	/// </summary>
	internal ref struct Delivery<TRightValue, TContainer> : IJoinedResultContainer<TLeftKey, TRightKey, TRightValue>
		where TContainer : struct, IJoinedResultContainer<TLeftKey, TRightValue>, allows ref struct {
		public TContainer Inner;
		// Not readonly on purpose: ValueSet has no readonly members, so a readonly field would make every
		// IndexOf copy the whole struct (inline storage included) before probing it.
		private ValueSet<JoinedKeyPair<TLeftKey, TRightKey>, DefaultKeyComparer<JoinedKeyPair<TLeftKey, TRightKey>>> _pairs;
		private readonly int[] _heads;
		private readonly TLeftKey[] _nodeLeft;
		private readonly int[] _nodeNext;

		/// <param name="inner">The resolver's result container.</param>
		/// <param name="survivingPairs">Snapshot of the paired core's pair set after the user filter ran.</param>
		/// <param name="heads">Per pair slot: the first chain node, or <see cref="NoChain"/>.</param>
		/// <param name="nodeLefts">Chain nodes: a left recorded for the right after its first one.</param>
		/// <param name="nodeNexts">Chain nodes: the next node of the same right, or <see cref="NoChain"/>.</param>
		public Delivery(TContainer inner,
			ValueSet<JoinedKeyPair<TLeftKey, TRightKey>, DefaultKeyComparer<JoinedKeyPair<TLeftKey, TRightKey>>> survivingPairs,
			int[] heads, TLeftKey[] nodeLefts, int[] nodeNexts) {
			Inner = inner;
			_pairs = survivingPairs;
			_heads = heads;
			_nodeLeft = nodeLefts;
			_nodeNext = nodeNexts;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		public void Add(TLeftKey firstLeft, TRightKey right, TRightValue value) {
			// The pair's JoinedKey is the first left that recorded the right; it always receives.
			Inner.Add(firstLeft, value);

			// Pair identity is the right key, so the probe's JoinedKey is irrelevant; the slot is the one
			// the right took when it was recorded — removals never renumber survivors.
			var slot = _pairs.IndexOf(new JoinedKeyPair<TLeftKey, TRightKey>(firstLeft, right));
			Debug.Assert(slot >= 0, "a delivered right must sit in the pair set");
			if (slot < 0) {
				return;
			}

			for (var node = _heads[slot]; node != NoChain; node = _nodeNext[node]) {
				Inner.Add(_nodeLeft[node], value);
			}
		}
	}
}
