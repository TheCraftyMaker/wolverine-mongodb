using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class dead_letter_edit_replay
{
    private readonly AppFixture _fixture;
    public dead_letter_edit_replay(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task edit_and_replay_tolerates_a_bodyless_poison_letter()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var dlqCollection = _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<DeadLetterMessage>(MongoConstants.DeadLetterCollection);

        var id = Guid.NewGuid();
        await dlqCollection.InsertOneAsync(new DeadLetterMessage
        {
            Id = id,
            MessageType = "poison",
            Replayable = false,
            Body = []
        });

        var newBody = "hello"u8.ToArray();
        await store.DeadLetters.EditAndReplayAsync(id, newBody, CancellationToken.None);

        var doc = await dlqCollection.Find(Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, id)).SingleAsync();
        doc.Replayable.ShouldBeTrue();

        var envelope = await store.DeadLetters.DeadLetterEnvelopeByIdAsync(id);
        envelope.ShouldNotBeNull();
        envelope!.Envelope.Data.ShouldBe(newBody);
    }

    [Fact]
    public async Task edit_and_replay_still_works_for_a_normal_bodied_letter()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("local://edit-replay");
        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Inbox.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("boom"));

        var newBody = "updated"u8.ToArray();
        await store.DeadLetters.EditAndReplayAsync(envelope.Id, newBody, CancellationToken.None);

        var dlqCollection = _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<DeadLetterMessage>(MongoConstants.DeadLetterCollection);
        var doc = await dlqCollection.Find(Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, envelope.Id)).SingleAsync();
        doc.Replayable.ShouldBeTrue();

        var replayed = await store.DeadLetters.DeadLetterEnvelopeByIdAsync(envelope.Id);
        replayed.ShouldNotBeNull();
        replayed!.Envelope.Data.ShouldBe(newBody);
    }
}
