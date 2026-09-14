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

    /// <summary>
    /// The <b>default</b> collection name for a saga type: <c>wolverine_saga_</c> plus the lowercased
    /// simple type name.
    /// </summary>
    /// <remarks>
    /// This is a pure function of the type and is deliberately frozen — changing it would silently
    /// rename live collections. Note its precondition: <c>Type.Name</c> carries no namespace, no
    /// generic arguments and no case, so two saga types with the same simple name resolve here to the
    /// same collection. <c>MongoCollectionNaming</c> is the resolution point that layers explicit
    /// per-type mappings (<c>MongoDbPersistenceOptions.MapSagaCollection</c>) and startup collision
    /// detection over this default; library code resolves through it, never through this method.
    /// </remarks>
    public static string SagaCollectionName(Type sagaType)
        => $"{SagaCollectionPrefix}{sagaType.Name.ToLowerInvariant()}";

    // Generic entity persistence (the [Entity]/IStorageAction<T> surface): one collection per
    // entity type, named by the lowercased type name and DELIBERATELY un-prefixed. Entity
    // collections are application-owned state, not Wolverine system collections, so prefixing
    // (wolverine_entity_todo) would be misleading and non-idiomatic. The frame helpers
    // (MongoEntityOperations) and any test/demo direct readers MUST resolve the name through this
    // method — never a hard-coded literal — so the write and read sides stay coupled. Because the
    // name is un-prefixed, ClearAllAsync's "wolverine_saga_" sweep never touches entity collections.
    /// <summary>
    /// The <b>default</b> collection name for a generic (non-saga) entity type: the lowercased simple
    /// type name, deliberately un-prefixed.
    /// </summary>
    /// <remarks>
    /// This is a pure function of the type and is deliberately frozen — changing it would silently
    /// rename live collections. Note its precondition: <c>Type.Name</c> carries no namespace, no
    /// generic arguments and no case, so two entity types with the same simple name resolve here to the
    /// same collection. Because the name is also un-prefixed it shares the application's own collection
    /// namespace. <c>MongoCollectionNaming</c> is the resolution point that layers explicit per-type
    /// mappings (<c>MongoDbPersistenceOptions.MapEntityCollection</c>) and startup collision detection
    /// over this default; library code resolves through it, never through this method.
    /// </remarks>
    public static string EntityCollectionName(Type entityType)
        => entityType.Name.ToLowerInvariant();
}
