namespace Prague.Core;

using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using Collections;

public enum RangeValueType : byte {
	None,
	Than,
	ThanOrEqual
}

public readonly struct RangeValue<TIndexKey> where TIndexKey : IComparable<TIndexKey> {
	public readonly TIndexKey Value;
	public readonly RangeValueType Type;

	public RangeValue(RangeValueType type, TIndexKey value) {
		Value = value;
		Type = type;
	}

	[Pure]
	public void Deconstruct(out RangeValueType t, out TIndexKey v) => (t, v) = (Type, Value);
}


public interface IJoinResolver {
	static abstract bool IsSorter { get; }
	bool Inner { get; }

	internal void UnsafeExecuteWithAccessor<TAccessor>(ref TAccessor accessor, bool cloneOnAdd, bool shouldPool,
		ref QueryResultsDisposer disposer)
		where TAccessor : struct, IUnsafeValueAccessor, allows ref struct;

	internal void UnsafeSortResults<TFullResult>(ref QueryResults<TFullResult> results, int skip, int take)
		=> throw new InvalidOperationException("Join resolver is not sortable");

	internal void UnsafeSortResults<TKey, TFullResult>(ref ValueDictionary<TKey, TFullResult, DefaultKeyComparer<TKey>> results, int skip, int take)
		where TFullResult: struct, IJoinResult
		where TKey : notnull, IEquatable<TKey> => throw new InvalidOperationException("Join resolver is not sortable");

	/// <summary>
	/// Sorter-only seam: exposes the user comparer as an <see cref="IComparer{TLeft}"/> over the
	/// LEFT value when this sorter orders by the left value itself (TResult == TLeftValue).
	/// Default: not a sorter / not left-ordered. Callers must gate on the JIT-folded
	/// <see cref="IsSorter"/> so this default is never constrained-called on join resolver structs.
	/// </summary>
	internal bool TryGetLeftComparer<TLeft>(out IComparer<TLeft>? comparer) {
		comparer = null;
		return false;
	}

	/// <summary>
	/// Whether this resolver supports narrow-only inner execution
	/// (<see cref="UnsafeNarrowIndexedInner{TExecutor}"/>). JIT-folded per instantiation;
	/// the bounded top-K path probes this before committing to the bounded plan.
	/// </summary>
	static virtual bool SupportsNarrowOnly => false;

	/// <summary>
	/// Narrow-only inner execution: intersects the outer query's candidate set with the lefts
	/// that have a (filter-passing) right match, WITHOUT writing anything into a results map.
	/// Only invoked by the bounded top-K path, and only on resolvers whose
	/// <see cref="SupportsNarrowOnly"/> is true.
	/// </summary>
	internal void UnsafeNarrowIndexedInner<TExecutor>(ref TExecutor leftQuery)
		where TExecutor : struct, IUnsafeCandidatesExecutor
		=> throw new InvalidOperationException("Resolver does not support narrow-only inner execution");

	/// <summary>
	/// Bounded-path fill for an inner resolver whose membership was already decided by
	/// <see cref="UnsafeNarrowIndexedInner{TExecutor}"/>: fetches the right values for the selected
	/// page rows WITHOUT re-applying the join filter (membership is settled; re-applying it would
	/// invoke the user's lambda a second time), then drops any page row whose slot came back empty
	/// — a right row retired between the two passes must not surface on an inner join.
	/// Only invoked on resolvers whose <see cref="SupportsNarrowOnly"/> is true.
	/// </summary>
	internal void UnsafeFillNarrowedInner<TAccessor>(ref TAccessor accessor, bool cloneOnAdd, bool shouldPool,
		ref QueryResultsDisposer disposer)
		where TAccessor : struct, IUnsafeValueAccessor, allows ref struct
		=> throw new InvalidOperationException("Resolver does not support narrow-only inner execution");

	void UnsafeExecuteIndexedInner<TAccessor, TExecutor>(
		ref TAccessor accessor,
		ref TExecutor leftQuery,
		bool cloneOnAdd,
		bool isFirst,
		ref QueryResultsDisposer disposer)
		where TExecutor : struct, IUnsafeCandidatesExecutor
		where TAccessor : struct, IUnsafeValueAccessor, allows ref struct;

	void PrepareIndexedInner<TExecutor>(
		ref TExecutor leftQuery,
		bool cloneOnAdd,
		bool shouldPool,
		ref QueryResultsDisposer disposer) where TExecutor : struct, IUnsafeCandidatesExecutor;

	static abstract void Clone<TFullResult>(int index, ref TFullResult value) where TFullResult : struct, IJoinResult;
}
public interface IJoinResolver<TLeftKey, TLeftValue> : IJoinResolver
	where TLeftKey : notnull, IEquatable<TLeftKey> {



}

/// <summary>
/// Interface for unified join resolvers that can handle both One and Many joins.
/// </summary>
public interface IJoinResolver<TLeftKey, TLeftValue, TRightValue> : IJoinResolver<TLeftKey, TLeftValue>, ICloner<TRightValue>
	where TLeftKey : notnull, IEquatable<TLeftKey> where TLeftValue : ICacheEquatable<TLeftValue>, ICacheClonable<TLeftValue> {
	static abstract void CloneValue(ref TRightValue value);
}

/// <summary>
/// Interface for Many join resolvers that need keyed container initialization.
/// </summary>
internal interface IJoinManyResolver<TLeftKey, TLeftValue, TInnerValue>
	: IJoinResolver<TLeftKey, TLeftValue, QueryResults<TInnerValue>>
	where TLeftKey : notnull, IEquatable<TLeftKey> where TLeftValue : ICacheEquatable<TLeftValue>, ICacheClonable<TLeftValue> {
	internal void ExecuteReverseMany<TContainer>(ref TContainer container, ReadOnlySpan<TLeftKey> keys)
		where TContainer : struct, IJoinedKeyedResultContainer<TLeftKey, TInnerValue>, allows ref struct;
}

public interface ILeftValueAccessor<TLeftValue> {
	ReadOnlySpan<TLeftValue> Values { get; }
}

public interface IUnsafeValueAccessor {
	ref TRightValue GetValueRef<TKey, TRightValue>(TKey key) where TKey : IEquatable<TKey>;
	ref TRightValue GetValueRefOrAddDefault<TKey, TRightValue>(TKey key, out bool exists) where  TKey : IEquatable<TKey>;

	ReadOnlySpan<TKey> GetKeys<TKey>() where TKey : IEquatable<TKey>;

	/// <summary>
	/// Inner-join post-walk cleanup: drops result-map entries where THIS accessor's
	/// slot is null/default (i.e., this resolver didn't match the key — either it
	/// never wrote to that slot, or the slot was created earlier by a prior chained
	/// resolver), then narrows <paramref name="candidates"/> to the surviving keys.
	/// Single struct-dispatched pass; zero heap allocation.
	/// </summary>
	internal void RetainNonNullSlots<TKey, TRightValue>(ref ValueSet<TKey, DefaultKeyComparer<TKey>> candidates) where TKey : IEquatable<TKey>;

	/// <summary>
	/// JoinMany analog of <see cref="RetainNonNullSlots"/>: drops result-map entries
	/// where THIS accessor's slot is an empty <see cref="QueryResults{TInnerValue}"/>
	/// (Count == 0 — no rights matched OR filter rejected them all), then narrows
	/// <paramref name="candidates"/> to surviving keys. Used by InnerJoinMany.
	/// </summary>
	internal void RetainNonEmptyManySlots<TKey, TInnerValue>(ref ValueSet<TKey, DefaultKeyComparer<TKey>> candidates) where TKey : IEquatable<TKey>;
}
public interface IUnsafeValueAccessor<TLeftKey> : IUnsafeValueAccessor
	where TLeftKey : IEquatable<TLeftKey> {
	ReadOnlySpan<TLeftKey> Keys { get; }

	ref TRightValue GetValueRef<TRightValue>(TLeftKey key);
	ref TRightValue GetValueRefOrAddDefault<TRightValue>(TLeftKey key, out bool exists);
}
/// <summary>
/// Accessor interface for getting references to value slots by key.
/// </summary>
public interface IValueAccessor<TLeftKey, TRightValue>
	where TLeftKey : IEquatable<TLeftKey> {
	ReadOnlySpan<TLeftKey> Keys { get; }

	ref TRightValue GetValueRef(TLeftKey key);
	ref TRightValue GetValueRefOrAddDefault(TLeftKey key, out bool exists);
}


internal static class JoinedKeyPair {
	public static JoinedKeyPair<TJoinedKey, TKey> Create<TJoinedKey, TKey>(TJoinedKey joinedKey, TKey key)
		where TJoinedKey : notnull where TKey : notnull =>
		new(joinedKey, key);

	public static JoinedKeyPair<TJoinedKey, TKey> Create<TJoinedKey, TKey>(TKey key)
		where TJoinedKey : notnull where TKey : notnull =>
		new(default!, key);
}

internal struct JoinedKeyPair<TJoinedKey, TKey> : IEquatable<JoinedKeyPair<TJoinedKey, TKey>>
	where TJoinedKey : notnull
	where TKey : notnull {
	public TJoinedKey JoinedKey;
	public TKey Key;

	public JoinedKeyPair(TJoinedKey joinedKey, TKey key) {
		JoinedKey = joinedKey;
		Key = key;
	}

	public bool Equals(JoinedKeyPair<TJoinedKey, TKey> other) => Key.Equals(other.Key);

	public override bool Equals([NotNullWhen(true)] object? obj) =>
		obj is JoinedKeyPair<TJoinedKey, TKey> other && Equals(other);

	public override int GetHashCode() => Key.GetHashCode();

	public static IntoTrait Into = new();

	public static IntoTrait IntoKeyed(TJoinedKey key) => new(key);

	public static IntoJoinKeyTrait IntoJoinKey = new ();

	public struct IntoJoinKeyTrait : IInto<JoinedKeyPair<TJoinedKey, TKey>, TJoinedKey> {

		public IntoJoinKeyTrait() {
		}

		public TJoinedKey Into(JoinedKeyPair<TJoinedKey, TKey> i) => i.JoinedKey;

		public JoinedKeyPair<TJoinedKey, TKey> From(TJoinedKey into) => throw new InvalidOperationException();
	}

	public struct IntoTrait : IInto<TKey, JoinedKeyPair<TJoinedKey, TKey>> {
		private readonly TJoinedKey _key = default!;

		public IntoTrait(TJoinedKey key) {
			_key = key;
		}

		public JoinedKeyPair<TJoinedKey, TKey> Into(TKey from) => new(_key, from);

		public TKey From(JoinedKeyPair<TJoinedKey, TKey> into) => into.Key;
	}
}
