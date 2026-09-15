using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// MongoDB-specific facts around the WolverineFx 6.38 <c>INodeAgentPersistence</c> contract
/// (<c>MarkHealthCheckAsync</c> returning <c>bool</c>, <c>ReregisterNodeAsync</c>,
/// <c>TryClaimAssignmentAsync</c>). The generic behaviour is pinned by the inherited
/// <c>NodePersistenceCompliance</c> suite; these facts add what only this store can promise: the
/// node-number counter is untouched by a re-registration, the heartbeat really moves the timestamp,
/// and the claim is a single-insert atomic step.
/// </summary>
[Collection("mongodb")]
public class node_reregistration
{
    private readonly AppFixture _fixture;
    public node_reregistration(AppFixture fixture) => _fixture = fixture;

    private static WolverineNode node(string description, params string[] capabilities)
    {
        var n = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            ControlUri = new Uri($"dbcontrol://{description}"),
            Description = description,
            Version = new Version(4, 5, 6)
        };
        n.Capabilities.AddRange(capabilities.Select(x => new Uri(x)));
        return n;
    }

    [Fact]
    public async Task reregister_preserves_identity_and_does_not_consume_a_node_number()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();
        var ct = TestContext.Current.CancellationToken;

        var first = node("first", "red://", "blue://");
        var firstNumber = await store.Nodes.PersistAsync(first, ct);

        // A peer ejects the still-live node...
        await store.Nodes.DeleteAsync(first.NodeId, firstNumber);
        (await store.Nodes.MarkHealthCheckAsync(first, ct)).ShouldBeFalse();

        // ...and the node resurrects its own document with the identity it still uses in memory.
        await store.Nodes.ReregisterNodeAsync(first, ct);

        var resurrected = (await store.Nodes.LoadAllNodesAsync(ct)).Single();
        resurrected.NodeId.ShouldBe(first.NodeId);
        resurrected.AssignedNodeNumber.ShouldBe(firstNumber);
        resurrected.Capabilities.OrderBy(x => x.ToString())
            .ShouldBe([new Uri("blue://"), new Uri("red://")]);
        resurrected.Version.ShouldBe(new Version(4, 5, 6));
        resurrected.Description.ShouldBe("first");
        resurrected.ControlUri.ShouldBe(first.ControlUri);

        // The counter was not touched: the next registration gets exactly the next number, not the
        // one after a phantom allocation made by the re-registration.
        var second = node("second");
        var secondNumber = await store.Nodes.PersistAsync(second, ct);
        secondNumber.ShouldBe(firstNumber + 1);
    }

    [Fact]
    public async Task reregister_is_an_upsert_when_the_document_still_exists()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();
        var ct = TestContext.Current.CancellationToken;

        var n = node("shrinking", "red://", "blue://", "green://");
        var number = await store.Nodes.PersistAsync(n, ct);

        // NodeAgentController also re-persists to advertise a SHRUNK capability set after releasing
        // a stalled agent (GH-3888) — the same upsert, on an existing document.
        n.Capabilities.RemoveAll(x => x == new Uri("green://"));
        await store.Nodes.ReregisterNodeAsync(n, ct);

        var all = await store.Nodes.LoadAllNodesAsync(ct);
        all.Count.ShouldBe(1);
        all[0].AssignedNodeNumber.ShouldBe(number);
        all[0].Capabilities.OrderBy(x => x.ToString()).ShouldBe([new Uri("blue://"), new Uri("red://")]);
    }

    [Fact]
    public async Task heartbeat_moves_the_timestamp_of_an_existing_document()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();
        var ct = TestContext.Current.CancellationToken;

        var n = node("beating");
        await store.Nodes.PersistAsync(n, ct);
        var stale = DateTimeOffset.UtcNow.AddMinutes(-10);
        await store.Nodes.OverwriteHealthCheckTimeAsync(n.NodeId, stale);

        // The controller passes a skeleton (id only) on the hot path.
        (await store.Nodes.MarkHealthCheckAsync(new WolverineNode { NodeId = n.NodeId }, ct)).ShouldBeTrue();

        var reloaded = await store.Nodes.LoadNodeAsync(n.NodeId, ct);
        reloaded.ShouldNotBeNull();
        reloaded.LastHealthCheck.ShouldBeGreaterThan(stale.AddMinutes(5));
        reloaded.AssignedNodeNumber.ShouldBeGreaterThan(0);
        reloaded.Description.ShouldBe("beating", "the heartbeat must not overwrite the document with the skeleton");
    }

    [Fact]
    public async Task try_claim_takes_an_unowned_agent_refuses_a_peers_and_is_idempotent_for_the_owner()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();
        var ct = TestContext.Current.CancellationToken;

        var owner = node("owner");
        var peer = node("peer");
        await store.Nodes.PersistAsync(owner, ct);
        await store.Nodes.PersistAsync(peer, ct);
        var agent = new Uri("fake://one");

        (await store.Nodes.TryClaimAssignmentAsync(owner.NodeId, agent, ct)).ShouldBeTrue();
        (await store.Nodes.TryClaimAssignmentAsync(peer.NodeId, agent, ct)).ShouldBeFalse();
        (await store.Nodes.TryClaimAssignmentAsync(owner.NodeId, agent, ct)).ShouldBeTrue();

        var nodes = await store.Nodes.LoadAllNodesAsync(ct);
        nodes.Single(x => x.NodeId == owner.NodeId).ActiveAgents.ShouldBe([agent]);
        nodes.Single(x => x.NodeId == peer.NodeId).ActiveAgents.ShouldBeEmpty();

        // Exactly one assignment document exists for the agent URI — the claim never duplicated it.
        var count = await _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<AgentAssignmentDocument>(MongoConstants.NodeAssignmentCollection)
            .CountDocumentsAsync(Builders<AgentAssignmentDocument>.Filter.Eq(x => x.Id, agent.ToString()),
                cancellationToken: ct);
        count.ShouldBe(1);
    }

    [Fact]
    public async Task concurrent_claims_of_one_agent_produce_exactly_one_owner()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();
        var ct = TestContext.Current.CancellationToken;

        var nodes = Enumerable.Range(0, 10).Select(i => node($"racer-{i}")).ToList();
        foreach (var n in nodes)
        {
            await store.Nodes.PersistAsync(n, ct);
        }

        var agent = new Uri("fake://contested");
        var results = await Task.WhenAll(nodes.Select(n => store.Nodes.TryClaimAssignmentAsync(n.NodeId, agent, ct)));

        results.Count(x => x).ShouldBe(1, "the _id uniqueness of the assignment document makes the insert the atomic step");
        (await store.Nodes.LoadAllNodesAsync(ct)).Count(x => x.ActiveAgents.Contains(agent)).ShouldBe(1);
    }
}
