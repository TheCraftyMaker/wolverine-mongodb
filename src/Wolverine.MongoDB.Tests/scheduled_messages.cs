using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class scheduled_messages
{
    private readonly AppFixture _fixture;
    public scheduled_messages(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task query_and_cancel()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("rabbitmq://queue/s");
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
        await store.Inbox.StoreIncomingAsync(envelope);

        var results = await store.ScheduledMessages.QueryAsync(new ScheduledMessageQuery(), CancellationToken.None);
        results.TotalCount.ShouldBe(1);

        await store.ScheduledMessages.CancelAsync(
            new ScheduledMessageQuery { MessageIds = [envelope.Id] }, CancellationToken.None);

        (await store.ScheduledMessages.QueryAsync(new ScheduledMessageQuery(), CancellationToken.None))
            .TotalCount.ShouldBe(0);
    }
}
