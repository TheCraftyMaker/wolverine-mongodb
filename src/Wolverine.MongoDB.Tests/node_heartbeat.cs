using Shouldly;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class node_heartbeat
{
    private readonly AppFixture _fixture;
    public node_heartbeat(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task heartbeat_for_unknown_node_reregisters_with_a_real_node_number()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();
        await store.Nodes.ClearAllAsync(CancellationToken.None);

        var unknown = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            ControlUri = new Uri("dbcontrol://unknown"),
            Description = "newcomer"
        };

        // Per the Wolverine contract (mirrored from Postgres), a heartbeat for an unregistered
        // node re-registers it via PersistAsync rather than leaving a half-populated phantom.
        await store.Nodes.MarkHealthCheckAsync(unknown, CancellationToken.None);

        var reloaded = await store.Nodes.LoadNodeAsync(unknown.NodeId, CancellationToken.None);
        reloaded.ShouldNotBeNull();
        reloaded.Description.ShouldBe("newcomer");
        // PersistAsync assigns a real node number (>= 1); no stale/zero phantom slot.
        reloaded.AssignedNodeNumber.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task heartbeat_updates_known_node()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();
        await store.Nodes.ClearAllAsync(CancellationToken.None);

        var node = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            ControlUri = new Uri("dbcontrol://known"),
            Description = "real"
        };

        await store.Nodes.PersistAsync(node, CancellationToken.None);

        // Should not throw and should leave exactly the one registered node.
        await store.Nodes.MarkHealthCheckAsync(node, CancellationToken.None);

        var reloaded = await store.Nodes.LoadNodeAsync(node.NodeId, CancellationToken.None);
        reloaded.ShouldNotBeNull();
        (await store.Nodes.LoadAllNodesAsync(CancellationToken.None)).Count.ShouldBe(1);
    }
}
