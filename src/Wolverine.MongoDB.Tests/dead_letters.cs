using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class dead_letters
{
    private readonly AppFixture _fixture;
    public dead_letters(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task move_query_and_replay()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("rabbitmq://queue/dl");
        await store.Inbox.StoreIncomingAsync(envelope);

        await store.Inbox.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("boom"));

        var byId = await store.DeadLetters.DeadLetterEnvelopeByIdAsync(envelope.Id);
        byId.ShouldNotBeNull();

        var results = await store.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(), CancellationToken.None);
        results.TotalCount.ShouldBe(1);

        await store.DeadLetters.ReplayAsync(
            new DeadLetterEnvelopeQuery { MessageIds = [envelope.Id] }, CancellationToken.None);
    }
}
