using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class reassign_incoming
{
    private readonly AppFixture _fixture;
    public reassign_incoming(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task only_reassigns_globally_owned_envelopes()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        // One envelope already owned by another node (OwnerId = 5) must NOT be stolen.
        var owned = ObjectMother.Envelope();
        owned.Destination = new Uri("rabbitmq://queue/r");
        owned.OwnerId = 5;
        await store.Inbox.StoreIncomingAsync(owned);

        // One globally-owned envelope (OwnerId = 0 / AnyNode) is eligible for reassignment.
        var free = ObjectMother.Envelope();
        free.Destination = new Uri("rabbitmq://queue/r");
        free.OwnerId = MongoConstants.AnyNode;
        await store.Inbox.StoreIncomingAsync(free);

        await store.ReassignIncomingAsync(7, new[] { owned, free });

        var byId = await ReadOwner(store, owned.Id);
        byId.ShouldBe(5); // unchanged — already owned by node 5

        var freeOwner = await ReadOwner(store, free.Id);
        freeOwner.ShouldBe(7); // claimed by node 7
    }

    private static async Task<int> ReadOwner(MongoDbMessageStore store, Guid envelopeId)
    {
        var doc = await store.Incoming
            .Find(Builders<IncomingMessage>.Filter.Eq(x => x.EnvelopeId, envelopeId))
            .FirstAsync();
        return doc.OwnerId;
    }
}
