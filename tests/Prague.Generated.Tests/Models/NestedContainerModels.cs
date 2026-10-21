namespace Prague.Generated.Tests.Models;

using Prague.Core;

// Containers whose values or elements are themselves containers. The generator used to collect the inner container
// (List<string>, Dictionary<string, int>) as if it were a user type and emitted `this[] = source.this[]` for its indexer.
[DataCache]
public partial class NestedContainerEntity {
	[DataCacheKey] public int Id { get; set; }

	public Dictionary<string, List<string>> OptionsByCountry { get; set; } = new();
	public Dictionary<string, List<string>>? NullableOptionsByCountry { get; set; }
	public Dictionary<string, string[]> ArraysByKey { get; set; } = new();
	public Dictionary<string, HashSet<int>> SetsByKey { get; set; } = new();
	public Dictionary<string, Dictionary<string, int>> NestedDictionaries { get; set; } = new();
	public Dictionary<string, List<NestedContainerItem>> ItemsByKey { get; set; } = new();
	public List<List<string>> ListOfLists { get; set; } = new();
	public List<Dictionary<string, string>> ListOfDictionaries { get; set; } = new();
}

public class NestedContainerItem {
	public string Name { get; set; } = "";
	public List<string> Tags { get; set; } = new();
}
