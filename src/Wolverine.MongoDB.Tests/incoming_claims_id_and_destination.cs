using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// In MessageIdentity.IdAndDestination mode the inbox identity unit is the
/// (envelope id, destination) pair, so one envelope Guid can legitimately have a
/// document per destination. Recovery must claim exactly the documents it loaded:
/// claiming a page for one listener must leave a sibling destination's document
/// globally owned so its own listener can still recover it.
/// </summary>
[Collection("mongodb")]
public class incoming_claims_id_and_destination
{
    private static readonly Uri DestinationOne = new("rabbitmq://queue/one");
    private static readonly Uri DestinationTwo = new("rabbitmq://queue/two");

    private readonly AppFixture _fixture;
    public incoming_claims_id_and_destination(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task claiming_one_destination_leaves_the_sibling_globally_owned()
    {
        var store = BuildIdAndDestinationStore();
        await store.Admin.RebuildAsync();

        var (first, second) = await StoreSameIdAtTwoDestinations(store);

        await store.ReassignIncomingAsync(7, new[] { first });

        (await ReadOwner(store, first)).ShouldBe(7);
        (await ReadOwner(store, second)).ShouldBe(MongoConstants.AnyNode);
    }

    [Fact]
    public async Task sibling_destination_stays_recoverable_after_a_claim()
    {
        var store = BuildIdAndDestinationStore();
        await store.Admin.RebuildAsync();

        var (first, second) = await StoreSameIdAtTwoDestinations(store);

        await store.ReassignIncomingAsync(7, new[] { first });

        // Destination two was never claimed, so its own listener's recovery page
        // must still see it. Claiming it here would strand the message: owned by a
        // node that never enqueued it for destination two's listener.
        var page = await store.LoadPageOfGloballyOwnedIncomingAsync(DestinationTwo, 10);
        page.Select(x => x.Id).ShouldBe(new[] { second.Id });

        var claimedPage = await store.LoadPageOfGloballyOwnedIncomingAsync(DestinationOne, 10);
        claimedPage.ShouldBeEmpty();
    }

    [Fact]
    public async Task id_only_mode_still_claims_by_envelope_id()
    {
        // In IdOnly mode InboxIdentity(e) == e.Id.ToString(), so keying the claim on
        // the document _id is byte-equivalent to keying it on the envelope Guid.
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = DestinationOne;
        envelope.OwnerId = MongoConstants.AnyNode;
        await store.Inbox.StoreIncomingAsync(envelope);

        await store.ReassignIncomingAsync(7, new[] { envelope });

        (await ReadOwner(store, envelope)).ShouldBe(7);
    }

    private MongoDbMessageStore BuildIdAndDestinationStore()
    {
        var opts = new WolverineOptions();
        opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
        return new MongoDbMessageStore(_fixture.Client, AppFixture.DatabaseName, opts);
    }

    private static async Task<(Envelope, Envelope)> StoreSameIdAtTwoDestinations(MongoDbMessageStore store)
    {
        var first = ObjectMother.Envelope();
        first.Destination = DestinationOne;
        first.OwnerId = MongoConstants.AnyNode;
        await store.Inbox.StoreIncomingAsync(first);

        var second = ObjectMother.Envelope();
        second.Id = first.Id;
        second.Destination = DestinationTwo;
        second.OwnerId = MongoConstants.AnyNode;
        await store.Inbox.StoreIncomingAsync(second);

        return (first, second);
    }

    private static async Task<int> ReadOwner(MongoDbMessageStore store, Envelope envelope)
    {
        var doc = await store.Incoming
            .Find(Builders<IncomingMessage>.Filter.Eq(x => x.Id, store.InboxIdentity(envelope)))
            .FirstAsync();
        return doc.OwnerId;
    }
}
