using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class outgoing_recovery_contention
{
    private readonly AppFixture _fixture;
    public outgoing_recovery_contention(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task recovery_does_not_reassign_envelopes_claimed_by_a_competitor_mid_flight()
    {
        await _fixture.ClearAll();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.PublishAllMessages().ToLocalQueue("contended-out").UseDurableInbox();
            }).StartAsync();

        var runtime = host.GetRuntime();
        var store = _fixture.BuildMessageStore();
        var destination = runtime.Endpoints
            .GetOrBuildSendingAgent(new Uri($"{TransportConstants.Local}://contended-out")).Destination;

        // Two orphaned envelopes at the same destination.
        var mine = ObjectMother.Envelope();
        mine.Destination = destination;
        mine.DeliverBy = DateTimeOffset.UtcNow.AddMinutes(5);
        await store.Outbox.StoreOutgoingAsync(mine, MongoConstants.AnyNode);

        var stolen = ObjectMother.Envelope();
        stolen.Destination = destination;
        stolen.DeliverBy = DateTimeOffset.UtcNow.AddMinutes(5);
        await store.Outbox.StoreOutgoingAsync(stolen, MongoConstants.AnyNode);

        // Simulate a competing node winning the race for one envelope between
        // this node's load and claim: flip its owner directly.
        const int competitorNode = 999;
        var outgoing = _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<OutgoingMessage>(MongoConstants.OutgoingCollection);
        await outgoing.UpdateOneAsync(
            Builders<OutgoingMessage>.Filter.Eq(x => x.Id, stolen.Id),
            Builders<OutgoingMessage>.Update.Set(x => x.OwnerId, competitorNode));

        await store.RecoverOrphanedOutgoingAsync(runtime, CancellationToken.None);

        // The competitor's envelope must remain untouched; ours must be claimed.
        var stolenDoc = await outgoing.Find(Builders<OutgoingMessage>.Filter.Eq(x => x.Id, stolen.Id)).SingleAsync();
        stolenDoc.OwnerId.ShouldBe(competitorNode,
            "an envelope owned by a live competitor must never be re-claimed");

        var mineDoc = await outgoing.Find(Builders<OutgoingMessage>.Filter.Eq(x => x.Id, mine.Id)).SingleAsync();
        mineDoc.OwnerId.ShouldBe(runtime.DurabilitySettings.AssignedNodeNumber);
    }
}
