// docs: the global last-update index shared by Customer (group key = its primary key) and Order
// (group key = the CustomerId foreign key). "Everything that changed for customer 42 since T."
namespace Prague.Samples.Models;

using Prague.Core;

public partial class CustomerActivityIndex : IDataCacheGlobalLastUpdateIndex<int>;
