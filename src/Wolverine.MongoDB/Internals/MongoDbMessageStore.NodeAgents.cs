using MongoDB.Driver;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : INodeAgentPersistence
{
    private IMongoCollection<NodeDocument> NodeDocs { get; }
    private IMongoCollection<AgentAssignmentDocument> AssignmentDocs { get; }
    private IMongoCollection<NodeRecordDocument> RecordDocs { get; }
    private IMongoCollection<AgentRestrictionDocument> RestrictionDocs { get; }
    private IMongoCollection<NodeCounterDocument> Counters { get; }

    public async Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        var counter = await Counters.FindOneAndUpdateAsync(
            Builders<NodeCounterDocument>.Filter.Eq(x => x.Id, MongoConstants.NodeCounterId),
            Builders<NodeCounterDocument>.Update.Inc(x => x.Count, 1),
            new FindOneAndUpdateOptions<NodeCounterDocument> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
            cancellationToken);

        node.AssignedNodeNumber = counter.Count;
        var doc = NodeDocument.FromWolverineNode(node);
        await NodeDocs.ReplaceOneAsync(Builders<NodeDocument>.Filter.Eq(x => x.Id, node.NodeId), doc,
            new ReplaceOptions { IsUpsert = true }, cancellationToken);
        return node.AssignedNodeNumber;
    }

    public async Task DeleteAsync(Guid nodeId, int assignedNodeNumber)
    {
        await NodeDocs.DeleteOneAsync(Builders<NodeDocument>.Filter.Eq(x => x.Id, nodeId));
        await AssignmentDocs.DeleteManyAsync(Builders<AgentAssignmentDocument>.Filter.Eq(x => x.NodeId, nodeId));
        await ReleaseAllOwnershipAsync(assignedNodeNumber);
    }

    public async Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken)
    {
        var nodesTask = NodeDocs.Find(FilterDefinition<NodeDocument>.Empty).ToListAsync(cancellationToken);
        var assignmentsTask = AssignmentDocs.Find(FilterDefinition<AgentAssignmentDocument>.Empty).ToListAsync(cancellationToken);
        await Task.WhenAll(nodesTask, assignmentsTask);

        var assignmentsByNode = assignmentsTask.Result.ToLookup(a => a.NodeId);
        return nodesTask.Result.Select(n =>
        {
            var w = n.ToWolverineNode();
            w.ActiveAgents = assignmentsByNode[n.Id].Select(a => new Uri(a.AgentUri)).ToList();
            return w;
        }).ToList();
    }

    public async Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken)
    {
        var doc = await NodeDocs.Find(Builders<NodeDocument>.Filter.Eq(x => x.Id, nodeId)).FirstOrDefaultAsync(cancellationToken);
        if (doc is null)
        {
            return null;
        }

        var w = doc.ToWolverineNode();
        var assignments = await AssignmentDocs.Find(Builders<AgentAssignmentDocument>.Filter.Eq(x => x.NodeId, nodeId)).ToListAsync(cancellationToken);
        w.ActiveAgents = assignments.Select(a => new Uri(a.AgentUri)).ToList();
        return w;
    }

    /// <summary>
    /// Refreshes this node's heartbeat and reports whether its document was there to refresh.
    /// <para>
    /// A miss means a peer deleted this still-live node's document (stale-node ejection under churn,
    /// GH-3604). The store must NOT insert anything here: <c>NodeAgentController</c> passes only a
    /// skeleton node to the heartbeat and, on <c>false</c>, re-registers the node's REAL identity via
    /// <see cref="ReregisterNodeAsync"/> and restores its agent assignments itself. Before WolverineFx
    /// 6.38 this method re-inserted a skeleton with a fresh node number and no capabilities, which
    /// dropped the live node out of capability-matched distribution.
    /// </para>
    /// </summary>
    public async Task<bool> MarkHealthCheckAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        var result = await NodeDocs.UpdateOneAsync(
            Builders<NodeDocument>.Filter.Eq(x => x.Id, node.NodeId),
            Builders<NodeDocument>.Update.Set(x => x.LastHealthCheck, DateTime.UtcNow),
            cancellationToken: cancellationToken);

        return result.MatchedCount > 0;
    }

    /// <summary>
    /// Re-persists a node document that was deleted out from under a still-live node. Unlike
    /// <see cref="PersistAsync"/> this never touches the node-number counter: the document is written
    /// with the node's existing <see cref="WolverineNode.AssignedNodeNumber"/>, <see cref="WolverineNode.Capabilities"/>
    /// and <see cref="WolverineNode.Version"/>, so the resurrected row matches the identity the process
    /// is still using in memory (envelope ownership, capability-matched agent distribution). Upsert on
    /// the node id; the caller restores agent assignments separately.
    /// </summary>
    public Task ReregisterNodeAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        var doc = NodeDocument.FromWolverineNode(node);
        doc.LastHealthCheck = DateTime.UtcNow;
        return NodeDocs.ReplaceOneAsync(Builders<NodeDocument>.Filter.Eq(x => x.Id, node.NodeId), doc,
            new ReplaceOptions { IsUpsert = true }, cancellationToken);
    }

    public Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime)
        => NodeDocs.UpdateOneAsync(
            Builders<NodeDocument>.Filter.Eq(x => x.Id, nodeId),
            Builders<NodeDocument>.Update.Set(x => x.LastHealthCheck, lastHeartbeatTime.UtcDateTime));

    public Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken)
    {
        if (agents.Count == 0)
        {
            return Task.CompletedTask;
        }

        var models = agents.Select(uri => new ReplaceOneModel<AgentAssignmentDocument>(
            Builders<AgentAssignmentDocument>.Filter.Eq(x => x.Id, uri.ToString()),
            new AgentAssignmentDocument { Id = uri.ToString(), NodeId = nodeId, AgentUri = uri.ToString() })
        { IsUpsert = true }).ToList();
        return AssignmentDocs.BulkWriteAsync(models, cancellationToken: cancellationToken);
    }

    public Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
        => AssignmentDocs.ReplaceOneAsync(
            Builders<AgentAssignmentDocument>.Filter.Eq(x => x.Id, agentUri.ToString()),
            new AgentAssignmentDocument { Id = agentUri.ToString(), NodeId = nodeId, AgentUri = agentUri.ToString() },
            new ReplaceOptions { IsUpsert = true }, cancellationToken);

    /// <summary>
    /// Claims <paramref name="agentUri"/> for <paramref name="nodeId"/> only if no node owns it yet
    /// (GH-4407). Unlike <see cref="AddAssignmentAsync"/> this never takes over a document a peer wrote:
    /// the insert relies on the <c>_id</c> (= agent URI) uniqueness, and on a duplicate key the existing
    /// document decides — <c>true</c> when it already names this node, <c>false</c> when another node owns
    /// it. A single insert plus one read; the insert is the atomic step.
    /// </summary>
    public async Task<bool> TryClaimAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
    {
        var id = agentUri.ToString();
        try
        {
            await AssignmentDocs.InsertOneAsync(
                new AgentAssignmentDocument { Id = id, NodeId = nodeId, AgentUri = id },
                cancellationToken: cancellationToken);
            return true;
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return await AssignmentDocs
                .Find(Builders<AgentAssignmentDocument>.Filter.And(
                    Builders<AgentAssignmentDocument>.Filter.Eq(x => x.Id, id),
                    Builders<AgentAssignmentDocument>.Filter.Eq(x => x.NodeId, nodeId)))
                .Limit(1)
                .AnyAsync(cancellationToken);
        }
    }

    // Node-scoped by design, unlike the two writes above. The collection holds exactly one document
    // per agent URI (_id = the URI), so ownership transfers by overwriting that document's nodeId —
    // which is why AssignAgentsAsync/AddAssignmentAsync upsert unscoped, matching Postgres's
    // "on conflict (id) do update set node_id". A delete keyed on the URI alone would therefore let a
    // node that no longer owns an agent wipe the row that now belongs to a *different* node.
    // Wolverine's caller, NodeAgentController.StopAgentAsync, always passes its own UniqueNodeId
    // ("remove MY claim") and issues the removal even when this node was never running the agent (it
    // sits outside the Agents.TryGetValue guard). This scoping is a hard requirement rather than
    // defence in depth: core carries a removeAssignment: false path (NodeAgentController.cs:511,
    // called from Reconcile.cs:176) because an unscoped delete let a leader read the agent as
    // unassigned and place it again, starting a duplicate. Every provider now filters on both, the
    // five SQL ones on id and node_id, RavenDb and Cosmos on a NodeId comparison added for GH-4407;
    // a mismatch is a silent no-op everywhere. Do NOT node-scope the writes above — their unscoped upsert IS how ownership
    // transfers, and the NodePersistenceCompliance assignment facts depend on it.
    public Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
        => AssignmentDocs.DeleteOneAsync(
            Builders<AgentAssignmentDocument>.Filter.And(
                Builders<AgentAssignmentDocument>.Filter.Eq(x => x.Id, agentUri.ToString()),
                Builders<AgentAssignmentDocument>.Filter.Eq(x => x.NodeId, nodeId)),
            cancellationToken);

    public async Task<NodeAgentState> LoadNodeAgentStateAsync(CancellationToken cancellationToken)
    {
        var nodes = await LoadAllNodesAsync(cancellationToken);
        var restrictions = await RestrictionDocs.Find(FilterDefinition<AgentRestrictionDocument>.Empty).ToListAsync(cancellationToken);
        var converted = restrictions.Select(r => new AgentRestriction(
            Guid.Parse(r.Id.Split('|').Last()), new Uri(r.AgentUri), r.Type, r.NodeNumber)).ToArray();
        return new NodeAgentState(nodes, new AgentRestrictions(converted));
    }

    public Task PersistAgentRestrictionsAsync(IReadOnlyList<AgentRestriction> restrictions, CancellationToken cancellationToken)
    {
        if (restrictions.Count == 0)
        {
            return Task.CompletedTask;
        }

        var models = restrictions.Select(r =>
        {
            var id = $"restriction|{r.Id}";
            var filter = Builders<AgentRestrictionDocument>.Filter.Eq(x => x.Id, id);
            return r.Type == AgentRestrictionType.None
                ? (WriteModel<AgentRestrictionDocument>)new DeleteOneModel<AgentRestrictionDocument>(filter)
                : new ReplaceOneModel<AgentRestrictionDocument>(filter,
                    new AgentRestrictionDocument { Id = id, AgentUri = r.AgentUri.ToString(), Type = r.Type, NodeNumber = r.NodeNumber })
                { IsUpsert = true };
        }).ToList();

        return RestrictionDocs.BulkWriteAsync(models, cancellationToken: cancellationToken);
    }

    public Task LogRecordsAsync(params NodeRecord[] records)
    {
        if (records.Length == 0)
        {
            return Task.CompletedTask;
        }

        return RecordDocs.InsertManyAsync(records.Select(NodeRecordDocument.FromRecord));
    }

    public async Task<IReadOnlyList<NodeRecord>> FetchRecentRecordsAsync(int count)
    {
        var docs = await RecordDocs.Find(FilterDefinition<NodeRecordDocument>.Empty)
            .Sort(Builders<NodeRecordDocument>.Sort.Descending(x => x.Timestamp))
            .Limit(count).ToListAsync();
        docs.Reverse();
        return docs.Select(d => d.ToRecord()).ToList();
    }

    /// <summary>
    /// One node-record housekeeping pass, honouring the same two settings the RDBMS
    /// <c>DurabilityAgent.PruneNodeRecords</c> honours (GH-3701): first the age bound
    /// (<see cref="DurabilitySettings.NodeEventRecordExpirationTime"/>, records older than that are
    /// deleted), then the row cap (<see cref="DurabilitySettings.NodeRecordRetention"/>, kept only when
    /// positive — an age bound alone puts no ceiling on a chatty cluster's record volume). Returns how many
    /// records the age bound removed. The pre-existing 14-day TTL index on <c>timestamp</c> stays as a
    /// server-side backstop; this pass is what applies the configured values.
    /// </summary>
    internal async Task<long> PruneNodeRecordsAsync(DurabilitySettings settings, CancellationToken token)
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(settings.NodeEventRecordExpirationTime);
        var expired = await RecordDocs.DeleteManyAsync(
            Builders<NodeRecordDocument>.Filter.Lt(x => x.Timestamp, cutoff), token);

        if (settings.NodeRecordRetention > 0)
        {
            await DeleteOldNodeRecordsAsync(settings.NodeRecordRetention);
        }

        return expired.DeletedCount;
    }

    public async Task DeleteOldNodeRecordsAsync(int retainCount)
    {
        if (retainCount <= 0) return;

        // Find the timestamp of the oldest record we want to KEEP, then delete
        // everything strictly older.
        var newest = await RecordDocs.Find(FilterDefinition<NodeRecordDocument>.Empty)
            .Sort(Builders<NodeRecordDocument>.Sort.Descending(x => x.Timestamp))
            .Limit(retainCount)
            .ToListAsync();

        if (newest.Count < retainCount)
        {
            return; // fewer records than the retain count: nothing to trim
        }

        var cutoff = newest[^1].Timestamp;
        await RecordDocs.DeleteManyAsync(
            Builders<NodeRecordDocument>.Filter.Lt(x => x.Timestamp, cutoff));
    }

    // Intentionally narrow: clears only node + assignment state, the operational surface
    // INodeAgentPersistence owns. Counters/locks/node-records/agent-restrictions are left
    // alone. A full system reset is IMessageStoreAdmin.ClearAllAsync/RebuildAsync
    // (MongoDbMessageStore.Admin.cs), which clears all twelve system collections plus every
    // wolverine_saga_* collection — that is the method the test harness calls.
    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        await NodeDocs.DeleteManyAsync(FilterDefinition<NodeDocument>.Empty, cancellationToken);
        await AssignmentDocs.DeleteManyAsync(FilterDefinition<AgentAssignmentDocument>.Empty, cancellationToken);
    }
}
