namespace Wolverine.MongoDB;

/// <summary>
/// MongoDB-specific persistence tuning for Wolverine.MongoDB.
/// </summary>
public class MongoDbPersistenceOptions
{
    /// <summary>
    /// How long the leader / scheduled-job lock lease is held before it can be
    /// taken over by another node. Wolverine's leadership health checks renew the
    /// lease well within this window. Lower values speed up leader failover at the
    /// cost of more lock churn; clocks across nodes must be synchronized to well
    /// within this duration. Default: 1 minute.
    /// </summary>
    public TimeSpan LockLeaseDuration { get; set; } = TimeSpan.FromMinutes(1);
}
