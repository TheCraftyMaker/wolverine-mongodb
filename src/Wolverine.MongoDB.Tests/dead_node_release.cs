using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Tests;

/// <summary>
/// Two-tick confirmation semantics of <see cref="MongoDbMessageStore.ReleaseDeadNodeOwnershipAsync"/>.
/// Every fact drives the method directly, so "tick" means "one call" — fully deterministic, no
/// sleeps, no real node races. The dead set is per-store state, so each fact must reuse ONE store
/// instance across its ticks (a fresh <c>BuildMessageStore()</c> is a fresh observer with no
/// previous tick).
/// </summary>
[Collection("mongodb")]
public class dead_node_release
{
    private readonly AppFixture _fixture;
    public dead_node_release(AppFixture fixture) => _fixture = fixture;

    private IMongoCollection<NodeDocument> NodeDocs => _fixture.Client
        .GetDatabase(AppFixture.DatabaseName)
        .GetCollection<NodeDocument>(MongoConstants.NodeCollection);

    // Registers a node document for a CHOSEN number, bypassing the monotonic counter that
    // INodeAgentPersistence.PersistAsync allocates from. Needed because these facts must make a
    // node document appear for a number that is *already* an OwnerId in the envelope collections.
    private Task RegisterNodeDocumentFor(int nodeNumber) => NodeDocs.InsertOneAsync(new NodeDocument
    {
        Id = Guid.NewGuid(),
        AssignedNodeNumber = nodeNumber,
        ControlUri = "tcp://localhost:5999",
        Started = DateTimeOffset.UtcNow,
        LastHealthCheck = DateTime.UtcNow
    });

    private static async Task<int> IncomingOwnerOf(MongoDbMessageStore store, Guid envelopeId)
        => (await store.Admin.AllIncomingAsync()).Single(x => x.Id == envelopeId).OwnerId;

    private static async Task<int> OutgoingOwnerOf(MongoDbMessageStore store, Guid envelopeId)
        => (await store.Admin.AllOutgoingAsync()).Single(x => x.Id == envelopeId).OwnerId;

    private static async Task<Envelope> StoreIncomingOwnedBy(MongoDbMessageStore store, int ownerId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("local://dead-node-release");
        envelope.OwnerId = ownerId;
        await store.Inbox.StoreIncomingAsync(envelope);
        return envelope;
    }

    private static async Task<Envelope> StoreOutgoingOwnedBy(MongoDbMessageStore store, int ownerId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("local://dead-node-release-out");
        await store.Outbox.StoreOutgoingAsync(envelope, ownerId);
        return envelope;
    }

    [Fact]
    public async Task owner_with_no_node_doc_is_not_released_on_first_tick_only_second()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        // Node 901 owns work but has no wolverine_nodes document — it crashed.
        const int deadNumber = 901;
        var incoming = await StoreIncomingOwnedBy(store, deadNumber);
        var outgoing = await StoreOutgoingOwnedBy(store, deadNumber);

        // Tick 1: first observation only. Releasing here is exactly the race being fixed — a node
        // that registered and claimed between the live-read and the release write would be stripped.
        await store.ReleaseDeadNodeOwnershipAsync(CancellationToken.None);

        (await IncomingOwnerOf(store, incoming.Id)).ShouldBe(deadNumber,
            "a dead owner observed for the FIRST time must not be released yet");
        (await OutgoingOwnerOf(store, outgoing.Id)).ShouldBe(deadNumber,
            "a dead owner observed for the FIRST time must not be released yet");

        // Tick 2: same number still owned, still unregistered — confirmed dead for the whole
        // interval between the two ticks.
        await store.ReleaseDeadNodeOwnershipAsync(CancellationToken.None);

        (await IncomingOwnerOf(store, incoming.Id)).ShouldBe(MongoConstants.AnyNode,
            "a dead owner confirmed on a SECOND tick must be released");
        (await OutgoingOwnerOf(store, outgoing.Id)).ShouldBe(MongoConstants.AnyNode,
            "a dead owner confirmed on a SECOND tick must be released");
    }

    [Fact]
    public async Task owner_that_registers_a_node_between_ticks_is_never_released()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        const int registeringNumber = 902;
        var incoming = await StoreIncomingOwnedBy(store, registeringNumber);
        var outgoing = await StoreOutgoingOwnedBy(store, registeringNumber);

        // Tick 1: 902 is owned-but-unregistered, so it enters this tick's dead set.
        await store.ReleaseDeadNodeOwnershipAsync(CancellationToken.None);

        // ...and now its node document exists. In production this shape cannot arise from
        // registration (a node owns nothing before PersistAsync has written its document), which is
        // why the release must key on a positive In(confirmed) whitelist: whatever the reason a
        // number is live at tick 2, it drops out of the dead set and is not released.
        await RegisterNodeDocumentFor(registeringNumber);

        await store.ReleaseDeadNodeOwnershipAsync(CancellationToken.None);

        (await IncomingOwnerOf(store, incoming.Id)).ShouldBe(registeringNumber,
            "an owner whose node document appeared between the ticks must not be released");
        (await OutgoingOwnerOf(store, outgoing.Id)).ShouldBe(registeringNumber,
            "an owner whose node document appeared between the ticks must not be released");

        // A third tick proves the release is not merely deferred one more interval: the number is
        // live now, so it can never be confirmed.
        await store.ReleaseDeadNodeOwnershipAsync(CancellationToken.None);

        (await IncomingOwnerOf(store, incoming.Id)).ShouldBe(registeringNumber);
        (await OutgoingOwnerOf(store, outgoing.Id)).ShouldBe(registeringNumber);
    }

    [Fact]
    public async Task anynode_and_live_owners_never_touched_across_two_ticks()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        // A genuinely live node, registered through the real counter-allocating path.
        var liveNumber = await store.Nodes.PersistAsync(
            new WolverineNode { NodeId = Guid.NewGuid(), ControlUri = new Uri("tcp://localhost:5678") },
            CancellationToken.None);

        var ownedByLive = await StoreIncomingOwnedBy(store, liveNumber);
        var outgoingOwnedByLive = await StoreOutgoingOwnedBy(store, liveNumber);
        var globallyOwned = await StoreIncomingOwnedBy(store, MongoConstants.AnyNode);
        var outgoingGloballyOwned = await StoreOutgoingOwnedBy(store, MongoConstants.AnyNode);

        // A dead owner alongside them, so the second tick really does issue the release writes —
        // this fact must prove those writes are scoped to the confirmed numbers, not that they
        // were skipped altogether.
        const int deadNumber = 903;
        var orphan = await StoreIncomingOwnedBy(store, deadNumber);

        await store.ReleaseDeadNodeOwnershipAsync(CancellationToken.None);

        (await IncomingOwnerOf(store, ownedByLive.Id)).ShouldBe(liveNumber);
        (await OutgoingOwnerOf(store, outgoingOwnedByLive.Id)).ShouldBe(liveNumber);
        (await IncomingOwnerOf(store, globallyOwned.Id)).ShouldBe(MongoConstants.AnyNode);
        (await OutgoingOwnerOf(store, outgoingGloballyOwned.Id)).ShouldBe(MongoConstants.AnyNode);

        await store.ReleaseDeadNodeOwnershipAsync(CancellationToken.None);

        (await IncomingOwnerOf(store, orphan.Id)).ShouldBe(MongoConstants.AnyNode,
            "the confirmed dead owner is released on the second tick");
        (await IncomingOwnerOf(store, ownedByLive.Id)).ShouldBe(liveNumber,
            "a live node's in-flight incoming work is never released");
        (await OutgoingOwnerOf(store, outgoingOwnedByLive.Id)).ShouldBe(liveNumber,
            "a live node's in-flight outgoing work is never released");
        (await IncomingOwnerOf(store, globallyOwned.Id)).ShouldBe(MongoConstants.AnyNode,
            "AnyNode is not an owner and is never a release candidate");
        (await OutgoingOwnerOf(store, outgoingGloballyOwned.Id)).ShouldBe(MongoConstants.AnyNode,
            "AnyNode is not an owner and is never a release candidate");
    }
}
