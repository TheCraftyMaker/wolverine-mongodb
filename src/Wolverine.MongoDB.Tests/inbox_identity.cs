using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

[Collection("mongodb")]
public class inbox_identity
{
    private readonly AppFixture _fixture;
    public inbox_identity(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task default_id_only_dedupes_across_destinations()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var first = ObjectMother.Envelope();
        first.Destination = new Uri("rabbitmq://queue/A");
        await store.Inbox.StoreIncomingAsync(first);

        var second = ObjectMother.Envelope();
        second.Id = first.Id;
        second.Destination = new Uri("rabbitmq://queue/B");

        (await store.Inbox.ExistsAsync(second, CancellationToken.None)).ShouldBeTrue();
    }

    [Fact]
    public async Task id_and_destination_treats_distinct_destinations_as_distinct()
    {
        var opts = new WolverineOptions();
        opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
        var store = new MongoDbMessageStore(_fixture.Client, AppFixture.DatabaseName, opts);
        await store.Admin.RebuildAsync();

        var first = ObjectMother.Envelope();
        first.Destination = new Uri("rabbitmq://queue/A");
        await store.Inbox.StoreIncomingAsync(first);

        var second = ObjectMother.Envelope();
        second.Id = first.Id;
        second.Destination = new Uri("rabbitmq://queue/B");

        (await store.Inbox.ExistsAsync(second, CancellationToken.None)).ShouldBeFalse();
    }
}
