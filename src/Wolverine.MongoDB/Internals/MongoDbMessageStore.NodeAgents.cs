using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : INodeAgentPersistence
{
    public Task ClearAllAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<int> PersistAsync(WolverineNode node, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task DeleteAsync(Guid nodeId, int assignedNodeNumber) => throw new NotImplementedException();
    public Task<IReadOnlyList<WolverineNode>> LoadAllNodesAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task PersistAgentRestrictionsAsync(IReadOnlyList<AgentRestriction> restrictions, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<NodeAgentState> LoadNodeAgentStateAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task AssignAgentsAsync(Guid nodeId, IReadOnlyList<Uri> agents, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task RemoveAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task AddAssignmentAsync(Guid nodeId, Uri agentUri, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task<WolverineNode?> LoadNodeAsync(Guid nodeId, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task MarkHealthCheckAsync(WolverineNode node, CancellationToken cancellationToken) => throw new NotImplementedException();
    public Task OverwriteHealthCheckTimeAsync(Guid nodeId, DateTimeOffset lastHeartbeatTime) => throw new NotImplementedException();
    public Task LogRecordsAsync(params NodeRecord[] records) => throw new NotImplementedException();
    public Task<IReadOnlyList<NodeRecord>> FetchRecentRecordsAsync(int count) => throw new NotImplementedException();
    public bool HasLeadershipLock() => throw new NotImplementedException();
    public Task<bool> TryAttainLeadershipLockAsync(CancellationToken token) => throw new NotImplementedException();
    public Task ReleaseLeadershipLockAsync() => throw new NotImplementedException();
}
