using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

[Collection("mongodb")]
public class inbox
{
    private readonly AppFixture _fixture;
    public inbox(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task store_exists_then_duplicate_throws()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("rabbitmq://queue/in");

        await store.Inbox.StoreIncomingAsync(envelope);
        (await store.Inbox.ExistsAsync(envelope, CancellationToken.None)).ShouldBeTrue();

        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => store.Inbox.StoreIncomingAsync(envelope));
    }

    [Fact]
    public async Task mark_handled_sets_handled_count()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("rabbitmq://queue/in");
        await store.Inbox.StoreIncomingAsync(envelope);

        await store.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        (await store.Admin.FetchCountsAsync()).Handled.ShouldBe(1);
    }
}
