using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class dead_letter_replay
{
    private readonly AppFixture _fixture;
    public dead_letter_replay(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task replay_moves_message_back_to_the_inbox()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("rabbitmq://queue/replay");
        await store.Inbox.StoreIncomingAsync(envelope);

        await store.Inbox.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("boom"));

        // Sanity: now in DLQ, gone from inbox.
        (await store.Admin.FetchCountsAsync()).DeadLetter.ShouldBe(1);
        (await store.Admin.AllIncomingAsync()).Count.ShouldBe(0);

        // Flag it replayable.
        await store.DeadLetters.ReplayAsync(
            new DeadLetterEnvelopeQuery { MessageIds = [envelope.Id] }, CancellationToken.None);

        // The recovery-loop method that actually moves replayable docs back to the inbox.
        await store.ReplayDeadLettersAsync(CancellationToken.None);

        var incoming = await store.Admin.AllIncomingAsync();
        incoming.Count.ShouldBe(1);
        var moved = incoming.Single();
        moved.Id.ShouldBe(envelope.Id);
        moved.Status.ShouldBe(EnvelopeStatus.Incoming);
        moved.OwnerId.ShouldBe(0);

        var counts = await store.Admin.FetchCountsAsync();
        counts.Incoming.ShouldBe(1);
        counts.DeadLetter.ShouldBe(0);
    }

    [Fact]
    public async Task replay_converges_when_incoming_doc_already_exists()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        // Simulate the crash window: the envelope was already re-inserted into incoming
        // by a previous (crashed) replay pass, but the DLQ doc was not yet deleted.
        var stranded = ObjectMother.Envelope();
        stranded.Destination = new Uri("local://replay-crash");
        await store.Inbox.StoreIncomingAsync(stranded);
        await store.Inbox.MoveToDeadLetterStorageAsync(stranded, new InvalidOperationException("boom"));
        await store.DeadLetters.ReplayAsync(
            new DeadLetterEnvelopeQuery { MessageIds = [stranded.Id] }, CancellationToken.None);
        // Pre-insert the incoming doc to recreate the post-crash state.
        stranded.Status = EnvelopeStatus.Incoming;
        stranded.OwnerId = 0;
        await store.Inbox.StoreIncomingAsync(stranded);

        // A second replayable letter behind the poisoned one must also be processed.
        var second = ObjectMother.Envelope();
        second.Destination = new Uri("local://replay-crash");
        await store.Inbox.StoreIncomingAsync(second);
        await store.Inbox.MoveToDeadLetterStorageAsync(second, new InvalidOperationException("boom2"));
        await store.DeadLetters.ReplayAsync(
            new DeadLetterEnvelopeQuery { MessageIds = [second.Id] }, CancellationToken.None);

        await store.ReplayDeadLettersAsync(CancellationToken.None);

        // Both DLQ docs are gone, both envelopes are back in incoming, no exception escaped.
        (await store.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(), CancellationToken.None))
            .TotalCount.ShouldBe(0);
        var counts = await store.Admin.FetchCountsAsync();
        counts.Incoming.ShouldBe(2);
    }

    [Fact]
    public async Task replay_skips_and_unflags_bodyless_poison_dead_letters()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var dlqCollection = _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<DeadLetterMessage>(MongoConstants.DeadLetterCollection);
        await dlqCollection.InsertOneAsync(new DeadLetterMessage
        {
            Id = Guid.NewGuid(),
            MessageType = "poison",
            Replayable = true,
            Body = []
        });

        await store.ReplayDeadLettersAsync(CancellationToken.None);

        // The body-less letter cannot be replayed: it stays in the DLQ but is unflagged
        // so the loop does not retry it on every tick.
        var doc = await dlqCollection.Find(FilterDefinition<DeadLetterMessage>.Empty).SingleAsync();
        doc.Replayable.ShouldBeFalse();
    }
}
