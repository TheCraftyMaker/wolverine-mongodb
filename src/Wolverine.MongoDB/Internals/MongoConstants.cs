namespace Wolverine.MongoDB.Internals;

public static class MongoConstants
{
    public const string IncomingCollection = "wolverine_incoming_envelopes";
    public const string OutgoingCollection = "wolverine_outgoing_envelopes";
    public const string DeadLetterCollection = "wolverine_dead_letters";
    public const string NodeCollection = "wolverine_nodes";
    public const string NodeAssignmentCollection = "wolverine_node_assignments";
    public const string NodeRecordCollection = "wolverine_node_records";
    public const string AgentRestrictionCollection = "wolverine_agent_restrictions";
    public const string CounterCollection = "wolverine_counters";
    public const string LockCollection = "wolverine_locks";

    public const string LeaderLockId = "leader";
    public const string ScheduledLockId = "scheduled-jobs";
    public const string NodeCounterId = "node_number";

    // owner id meaning "any node" — matches Wolverine's TransportConstants.AnyNode (0)
    public const int AnyNode = 0;

    // Saga persistence: one MongoDB collection per saga type. The per-type collection is
    // idiomatic Mongo (no cross-type _id collision, independently indexable/enumerable) and
    // lets ClearAllAsync drop every saga collection by prefix between compliance facts.
    public const string SagaCollectionPrefix = "wolverine_saga_";

    public static string SagaCollectionName(Type sagaType)
        => $"{SagaCollectionPrefix}{sagaType.Name.ToLowerInvariant()}";
}
