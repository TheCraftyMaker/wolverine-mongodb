using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Tracking;
using Wolverine.Transports;

#pragma warning disable CS8981 // lowercase type name

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class outbox_recovery
{
    private readonly AppFixture _fixture;
    public outbox_recovery(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task recovers_orphaned_outgoing_by_reassigning_to_this_node()
    {
        await _fixture.ClearAll();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.PublishAllMessages().ToLocalQueue("durable-out").UseDurableInbox();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var store = _fixture.BuildMessageStore();

        // The sending agent for the durable local queue gives us a real destination URI.
        var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(
            new Uri($"{TransportConstants.Local}://durable-out"));
        var destination = sendingAgent.Destination;

        // Persist orphaned (globally owned) outgoing envelopes to that destination.
        var goodEnvelope = ObjectMother.Envelope();
        goodEnvelope.Destination = destination;
        goodEnvelope.DeliverBy = DateTimeOffset.UtcNow.AddMinutes(5);
        await store.Outbox.StoreOutgoingAsync(goodEnvelope, MongoConstants.AnyNode);

        // Sanity: it is owned by AnyNode (0) before recovery.
        var before = await store.Outbox.LoadOutgoingAsync(destination);
        before.Single().OwnerId.ShouldBe(0);

        await store.RecoverOrphanedOutgoingAsync(runtime, CancellationToken.None);

        var after = await store.Admin.AllOutgoingAsync();
        var nodeNumber = runtime.DurabilitySettings.AssignedNodeNumber;
        after.Single().OwnerId.ShouldBe(nodeNumber);
        nodeNumber.ShouldNotBe(0);

        // And the recovery feed itself must now be empty: the envelope is owned.
        (await store.Outbox.LoadOutgoingAsync(destination)).ShouldBeEmpty();
    }

    [Fact]
    public async Task recovery_discards_expired_outgoing()
    {
        await _fixture.ClearAll();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.PublishAllMessages().ToLocalQueue("durable-out").UseDurableInbox();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var store = _fixture.BuildMessageStore();

        var sendingAgent = runtime.Endpoints.GetOrBuildSendingAgent(
            new Uri($"{TransportConstants.Local}://durable-out"));
        var destination = sendingAgent.Destination;

        var expired = ObjectMother.Envelope();
        expired.Destination = destination;
        expired.DeliverBy = DateTimeOffset.UtcNow.AddMinutes(-5);
        await store.Outbox.StoreOutgoingAsync(expired, MongoConstants.AnyNode);

        await store.RecoverOrphanedOutgoingAsync(runtime, CancellationToken.None);

        // Expired message must be discarded, not reassigned.
        (await store.Admin.AllOutgoingAsync()).Count.ShouldBe(0);
    }
}
