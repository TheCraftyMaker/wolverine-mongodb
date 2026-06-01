using Shouldly;
using Wolverine.ComplianceTests;
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
}
