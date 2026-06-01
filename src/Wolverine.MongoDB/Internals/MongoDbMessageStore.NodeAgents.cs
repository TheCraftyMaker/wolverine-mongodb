using MongoDB.Driver;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : INodeAgentPersistence
{
    private IMongoCollection<NodeDocument> NodeDocs => _database.GetCollection<NodeDocument>(MongoConstants.NodeCollection);
    private IMongoCollection<AgentAssignmentDocument> AssignmentDocs => _database.GetCollection<AgentAssignmentDocument>(MongoConstants.NodeAssignmentCollection);
    private IMongoCollection<NodeRecordDocument> RecordDocs => _database.GetCollection<NodeRecordDocument>(MongoConstants.NodeRecordCollection);
    private IMongoCollection<AgentRestrictionDocument> RestrictionDocs => _database.GetCollection<AgentRestrictionDocument>(MongoConstants.AgentRestrictionCollection);
    private IMongoCollection<NodeCounterDocument> Counters => _database.GetCollection<NodeCounterDocument>(MongoConstants.CounterCollection);

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
        var nodes = await NodeDocs.Find(FilterDefinition<NodeDocument>.Empty).ToListAsync(cancellationToken);
        var assignments = await AssignmentDocs.Find(FilterDefinition<AgentAssignmentDocument>.Empty).ToListAsync(cancellationToken);
        return nodes.Select(n =>
        {
            var w = n.ToWolverineNode();
            w.ActiveAgents = assignments.Where(a => a.NodeId == n.Id).Select(a => new Uri(a.AgentUri)).ToList();
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

    // Mirror the canonical Postgres implementation: update the heartbeat in place, and if no
    // node document matched, re-register the node via PersistAsync. This avoids resurrecting a
    // phantom node from a blind SetOnInsert (which would have written a half-populated document
    // with a stale/zero node number); re-registration always assigns a proper node number and a
    // complete record. The Wolverine NodePersistenceCompliance suite enforces this upsert-style
    // contract (a heartbeat for an unregistered node must create it).
    public async Task MarkHealthCheckAsync(WolverineNode node, CancellationToken cancellationToken)
    {
        var result = await NodeDocs.UpdateOneAsync(
            Builders<NodeDocument>.Filter.Eq(x => x.Id, node.NodeId),
            Builders<NodeDocument>.Update.Set(x => x.LastHealthCheck, DateTime.UtcNow),
            cancellationToken: cancellationToken);

        if (result.MatchedCount == 0)
        {
            await PersistAsync(node, cancellationToken);
        }
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

    public Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken)
        => AssignmentDocs.DeleteOneAsync(Builders<AgentAssignmentDocument>.Filter.Eq(x => x.Id, agentUri.ToString()), cancellationToken);

    public async Task<NodeAgentState> LoadNodeAgentStateAsync(CancellationToken cancellationToken)
    {
        var nodes = await LoadAllNodesAsync(cancellationToken);
        var restrictions = await RestrictionDocs.Find(FilterDefinition<AgentRestrictionDocument>.Empty).ToListAsync(cancellationToken);
        var converted = restrictions.Select(r => new AgentRestriction(
            Guid.Parse(r.Id.Split('|').Last()), new Uri(r.AgentUri), r.Type, r.NodeNumber)).ToArray();
        return new NodeAgentState(nodes, new AgentRestrictions(converted));
    }

    public async Task PersistAgentRestrictionsAsync(IReadOnlyList<AgentRestriction> restrictions, CancellationToken cancellationToken)
    {
        foreach (var r in restrictions)
        {
            var id = $"restriction|{r.Id}";
            if (r.Type == AgentRestrictionType.None)
            {
                await RestrictionDocs.DeleteOneAsync(Builders<AgentRestrictionDocument>.Filter.Eq(x => x.Id, id), cancellationToken);
            }
            else
            {
                await RestrictionDocs.ReplaceOneAsync(
                    Builders<AgentRestrictionDocument>.Filter.Eq(x => x.Id, id),
                    new AgentRestrictionDocument { Id = id, AgentUri = r.AgentUri.ToString(), Type = r.Type, NodeNumber = r.NodeNumber },
                    new ReplaceOptions { IsUpsert = true }, cancellationToken);
            }
        }
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

    public async Task ClearAllAsync(CancellationToken cancellationToken)
    {
        await NodeDocs.DeleteManyAsync(FilterDefinition<NodeDocument>.Empty, cancellationToken);
        await AssignmentDocs.DeleteManyAsync(FilterDefinition<AgentAssignmentDocument>.Empty, cancellationToken);
    }
}
