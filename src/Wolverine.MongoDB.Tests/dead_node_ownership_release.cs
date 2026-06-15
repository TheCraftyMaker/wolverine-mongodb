using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class dead_node_ownership_release
{
    private readonly AppFixture _fixture;
    public dead_node_ownership_release(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task releases_incoming_and_outgoing_owned_by_unregistered_node_numbers()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        // A live node with number 1.
        var liveNode = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            ControlUri = new Uri("tcp://localhost:5678")
        };
        var liveNumber = await store.Nodes.PersistAsync(liveNode, CancellationToken.None);

        // Incoming owned by the live node — must be untouched.
        var ownedByLive = ObjectMother.Envelope();
        ownedByLive.Destination = new Uri("local://dead-node-test");
        ownedByLive.OwnerId = liveNumber;
        await store.Inbox.StoreIncomingAsync(ownedByLive);

        // Incoming + outgoing owned by node 999, which has no node document (it crashed).
        var orphanIncoming = ObjectMother.Envelope();
        orphanIncoming.Destination = new Uri("local://dead-node-test");
        orphanIncoming.OwnerId = 999;
        await store.Inbox.StoreIncomingAsync(orphanIncoming);

        var orphanOutgoing = ObjectMother.Envelope();
        orphanOutgoing.Destination = new Uri("local://dead-node-out");
        await store.Outbox.StoreOutgoingAsync(orphanOutgoing, 999);

        await store.ReleaseDeadNodeOwnershipAsync(CancellationToken.None);

        var incoming = await store.Admin.AllIncomingAsync();
        incoming.Single(x => x.Id == ownedByLive.Id).OwnerId.ShouldBe(liveNumber);
        incoming.Single(x => x.Id == orphanIncoming.Id).OwnerId.ShouldBe(0,
            "envelopes owned by a node number with no live node must be released");

        (await store.Admin.AllOutgoingAsync()).Single().OwnerId.ShouldBe(0);
    }
}
