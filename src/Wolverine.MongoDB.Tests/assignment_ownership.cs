using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Tests;

/// <summary>
/// Ownership semantics of <c>INodeAgentPersistence.RemoveAssignmentAsync(nodeId, agentUri, …)</c>.
/// <para>
/// <c>wolverine_node_assignments</c> holds exactly one document per agent URI (<c>_id</c> is the URI,
/// <c>nodeId</c> is the owner), so ownership transfers by *overwriting* that single document —
/// <c>AddAssignmentAsync</c>/<c>AssignAgentsAsync</c> upsert unscoped on purpose. The removal must
/// therefore be node-scoped: Wolverine's only caller, <c>NodeAgentController.StopAgentAsync</c>,
/// always passes its own <c>UniqueNodeId</c> ("remove *my* claim") and issues the removal even when
/// this node was never running the agent, so a removal from a node that no longer owns the agent
/// must not delete the row that now belongs to a different node.
/// </para>
/// <para>
/// Every fact drives <c>store.Nodes</c> directly — no hosts, no heartbeats, no timing.
/// </para>
/// </summary>
[Collection("mongodb")]
public class assignment_ownership
{
    private static readonly Uri TheAgent = new("blue://one");
    private static readonly Uri OtherAgent = new("blue://two");

    private readonly AppFixture _fixture;
    public assignment_ownership(AppFixture fixture) => _fixture = fixture;

    private IMongoCollection<AgentAssignmentDocument> AssignmentDocs => _fixture.Client
        .GetDatabase(AppFixture.DatabaseName)
        .GetCollection<AgentAssignmentDocument>(MongoConstants.NodeAssignmentCollection);

    private static async Task<Guid> RegisterNode(MongoDbMessageStore store, string description)
    {
        var node = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            ControlUri = new Uri($"dbcontrol://{description}"),
            Description = description
        };

        await store.Nodes.PersistAsync(node, CancellationToken.None);
        return node.NodeId;
    }

    // Reads the raw assignment document so a fact can distinguish "row gone" from "row re-owned".
    private async Task<Guid?> OwnerOf(Uri agentUri)
    {
        var doc = await AssignmentDocs
            .Find(Builders<AgentAssignmentDocument>.Filter.Eq(x => x.Id, agentUri.ToString()))
            .FirstOrDefaultAsync();
        return doc?.NodeId;
    }

    private Task<long> AssignmentCount()
        => AssignmentDocs.CountDocumentsAsync(FilterDefinition<AgentAssignmentDocument>.Empty);

    [Fact]
    public async Task removal_by_a_node_that_does_not_own_the_assignment_is_a_no_op()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var nodeA = await RegisterNode(store, "a");
        var nodeB = await RegisterNode(store, "b");

        await store.Nodes.AddAssignmentAsync(nodeB, TheAgent, CancellationToken.None);

        // A never owned this agent. StopAgentAsync issues the removal regardless of whether the
        // node was running the agent, so this shape has to be inert.
        await store.Nodes.RemoveAssignmentAsync(nodeA, TheAgent, CancellationToken.None);

        (await OwnerOf(TheAgent)).ShouldBe(nodeB,
            "a removal from a node that does not own the assignment must not delete the row");

        var persisted = await store.Nodes.LoadNodeAsync(nodeB, CancellationToken.None);
        persisted.ShouldNotBeNull();
        persisted.ActiveAgents.ShouldContain(TheAgent);
    }

    [Fact]
    public async Task a_stale_removal_after_ownership_moved_leaves_the_new_owner_intact()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var nodeA = await RegisterNode(store, "a");
        var nodeB = await RegisterNode(store, "b");

        // A owns the agent, then ownership transfers to B — the unscoped upsert overwrites the
        // one document rather than creating a second, which is exactly why the delete must be scoped.
        await store.Nodes.AddAssignmentAsync(nodeA, TheAgent, CancellationToken.None);
        await store.Nodes.AddAssignmentAsync(nodeB, TheAgent, CancellationToken.None);

        // A's stop finally lands, after the row already belongs to B.
        await store.Nodes.RemoveAssignmentAsync(nodeA, TheAgent, CancellationToken.None);

        (await OwnerOf(TheAgent)).ShouldBe(nodeB,
            "a stale removal from the previous owner must not delete the new owner's row");
        (await AssignmentCount()).ShouldBe(1,
            "ownership transfer overwrites the single document per agent URI");

        var nodes = await store.Nodes.LoadAllNodesAsync(CancellationToken.None);
        nodes.Single(x => x.NodeId == nodeB).ActiveAgents.ShouldContain(TheAgent);
        nodes.Single(x => x.NodeId == nodeA).ActiveAgents.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_owning_node_can_still_remove_its_own_assignment()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var nodeA = await RegisterNode(store, "a");

        await store.Nodes.AddAssignmentAsync(nodeA, TheAgent, CancellationToken.None);
        await store.Nodes.AddAssignmentAsync(nodeA, OtherAgent, CancellationToken.None);

        await store.Nodes.RemoveAssignmentAsync(nodeA, TheAgent, CancellationToken.None);

        // Guards against an over-scoped or mis-serialised predicate that never matches: the owner
        // must still be able to release its own claim.
        (await OwnerOf(TheAgent)).ShouldBeNull();

        var persisted = await store.Nodes.LoadNodeAsync(nodeA, CancellationToken.None);
        persisted.ShouldNotBeNull();
        persisted.ActiveAgents.ShouldBe([OtherAgent]);
    }

    [Fact]
    public async Task removing_an_assignment_that_was_never_written_is_a_no_op()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var nodeA = await RegisterNode(store, "a");

        await Should.NotThrowAsync(() =>
            store.Nodes.RemoveAssignmentAsync(nodeA, TheAgent, CancellationToken.None));

        (await AssignmentCount()).ShouldBe(0);
    }
}
