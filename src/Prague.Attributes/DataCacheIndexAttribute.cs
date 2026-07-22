// ReSharper disable once CheckNamespace
namespace Prague.Core;

/// <summary>
///   Creates an index on a property for fast lookup.
///   Can be applied directly to a property, or to a class when using DataCache&lt;T&gt; for external types.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Class, AllowMultiple = true)]
public sealed class DataCacheIndexAttribute : Attribute {
	/// <summary>
	///   Creates an index on the property this attribute is attached to.
	/// </summary>
	public DataCacheIndexAttribute() {
	}

	/// <summary>
	///   Creates an index on the property this attribute is attached to with the specified index type.
	/// </summary>
	/// <param name="indexType">The type of index to create.</param>
	public DataCacheIndexAttribute(DataCacheIndexType indexType) {
		IndexType = indexType;
	}

	/// <summary>
	///   Creates an index on a property of an external type (for use with DataCache&lt;T&gt;).
	/// </summary>
	/// <param name="propertyName">The name of the property to index. Use nameof() for compile-time safety.</param>
	/// <param name="indexType">The type of index to create.</param>
	public DataCacheIndexAttribute(string propertyName, DataCacheIndexType indexType) {
		PropertyName = propertyName;
		IndexType = indexType;
	}

	/// <summary>
	///   Creates a named index on a property of an external type (for use with DataCache&lt;T&gt;).
	/// </summary>
	/// <param name="propertyName">The name of the property to index. Use nameof() for compile-time safety.</param>
	/// <param name="indexName">The name of the index.</param>
	/// <param name="indexType">The type of index to create.</param>
	public DataCacheIndexAttribute(string propertyName, string indexName, DataCacheIndexType indexType) {
		PropertyName = propertyName;
		IndexName = indexName;
		IndexType = indexType;
	}

	/// <summary>
	///   The name of the property to index (for class-level usage with external types).
	/// </summary>
	public string? PropertyName { get; set; }

	/// <summary>
	///   The name of the index. If not specified, defaults to the property name.
	/// </summary>
	public string? IndexName { get; set; }

	/// <summary>
	///   The type of index to create.
	/// </summary>
	public DataCacheIndexType IndexType { get; set; } = DataCacheIndexType.Many;

	/// <summary>
	///   When <c>true</c> on a <see cref="DataCacheIndexType.Many"/> index, emit a
	///   <c>CacheSymmetricKeyValueListIndex</c> instead of <c>CacheKeyValueListIndex</c>.
	///   The symmetric variant supports reverse lookup (TKey → TIndexKey), required for
	///   index-driven joins like <c>JoinOne(leftIndex, rightCache)</c>.
	/// </summary>
	public bool Symmetric { get; set; } = false;

	/// <summary>
	///   Sizing hint for <see cref="DataCacheIndexType.Many"/> indexes: the expected number
	///   of values per index key, used as the slot capacity of the FIRST table each per-key
	///   bucket rents (rounded up to a prime). It is a hint, not a floor — buckets still
	///   start unallocated, grow past the hint on demand and never shrink below live content.
	///   Unset keeps default (127 slots per bucket)
	/// </summary>
	public int ExpectedValuesPerKey { get; set; } = 0;
}