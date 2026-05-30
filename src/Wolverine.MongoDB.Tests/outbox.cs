using Shouldly;
using Wolverine.ComplianceTests;

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

        await store.Outbox.StoreOutgoingAsync(envelope, 7);

        var loaded = await store.Outbox.LoadOutgoingAsync(envelope.Destination);
        loaded.Count.ShouldBe(1);
        loaded[0].Id.ShouldBe(envelope.Id);
        loaded[0].OwnerId.ShouldBe(7);

        await store.Outbox.DeleteOutgoingAsync(envelope);
        (await store.Outbox.LoadOutgoingAsync(envelope.Destination)).Count.ShouldBe(0);
    }
}
