using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;

#pragma warning disable CS8981 // lowercase type name

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class outbox
{
    private readonly AppFixture _fixture;
    public outbox(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task store_load_delete_round_trip()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("rabbitmq://queue/one");

        await store.Outbox.StoreOutgoingAsync(envelope, MongoConstants.AnyNode);

        var loaded = await store.Outbox.LoadOutgoingAsync(envelope.Destination);
        loaded.Count.ShouldBe(1);
        loaded[0].Id.ShouldBe(envelope.Id);
        loaded[0].OwnerId.ShouldBe(MongoConstants.AnyNode);

        await store.Outbox.DeleteOutgoingAsync(envelope);
        (await store.Outbox.LoadOutgoingAsync(envelope.Destination)).Count.ShouldBe(0);
    }

    [Fact]
    public async Task load_outgoing_only_returns_globally_owned_envelopes()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();
        var destination = new Uri("local://load-outgoing-owner-filter");

        var orphaned = ObjectMother.Envelope();
        orphaned.Destination = destination;
        await store.Outbox.StoreOutgoingAsync(orphaned, MongoConstants.AnyNode);

        var ownedByLiveNode = ObjectMother.Envelope();
        ownedByLiveNode.Destination = destination;
        await store.Outbox.StoreOutgoingAsync(ownedByLiveNode, 5);

        var loaded = await store.Outbox.LoadOutgoingAsync(destination);

        // Only the owner_id == 0 envelope may be returned; envelopes owned by a live
        // node are in flight and must never be handed to recovery.
        loaded.Count.ShouldBe(1);
        loaded.Single().Id.ShouldBe(orphaned.Id);
    }
}
