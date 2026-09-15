using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// The dead-node ownership sweep now honours the orphan-sweep settings upstream applies only in the
/// RDBMS durability agent (GH-3971): its own cadence (<c>OrphanedMessageSweepPollingTime</c>, Balanced
/// mode only) and bounded work per sweep (<c>OrphanedMessageReleaseBatchSize</c> ×
/// <c>OrphanedMessageReleaseMaxBatchesPerCycle</c>). The two-tick confirmation of
/// <c>dead_node_release.cs</c> is unchanged — "tick" is now one sweep — and these facts drive the
/// sweep directly so bounded work and progress are deterministic.
/// </summary>
[Collection("mongodb")]
public class orphan_sweep_settings
{
    private readonly AppFixture _fixture;
    public orphan_sweep_settings(AppFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MongoDbMessageStore buildStore(int batchSize, int maxBatches)
    {
        var options = new WolverineOptions();
        options.Durability.OrphanedMessageReleaseBatchSize = batchSize;
        options.Durability.OrphanedMessageReleaseMaxBatchesPerCycle = maxBatches;
        return new MongoDbMessageStore(_fixture.Client, AppFixture.DatabaseName, options);
    }

    private static async Task seedOwnedByDeadNodeAsync(MongoDbMessageStore store, int deadOwner, int incoming, int outgoing)
    {
        for (var i = 0; i < incoming; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Destination = new Uri("local://orphan-sweep");
            envelope.OwnerId = deadOwner;
            await store.Inbox.StoreIncomingAsync(envelope);
        }

        for (var i = 0; i < outgoing; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Destination = new Uri("local://orphan-sweep-out");
            await store.Outbox.StoreOutgoingAsync(envelope, deadOwner);
        }
    }

    private static async Task<int> stillOwnedAsync(MongoDbMessageStore store, int owner)
        => (await store.Admin.AllIncomingAsync()).Count(x => x.OwnerId == owner)
           + (await store.Admin.AllOutgoingAsync()).Count(x => x.OwnerId == owner);

    [Fact]
    public async Task a_sweep_releases_at_most_batch_size_times_max_batches_and_later_sweeps_finish_the_job()
    {
        await _fixture.ClearAll();
        // No node document for 77, so it is dead once observed on two consecutive sweeps.
        var store = buildStore(batchSize: 2, maxBatches: 1);
        await seedOwnedByDeadNodeAsync(store, 77, incoming: 5, outgoing: 0);

        (await store.ReleaseDeadNodeOwnershipAsync(Ct)).ShouldBe(0, "first observation only: nothing is confirmed dead yet");
        (await stillOwnedAsync(store, 77)).ShouldBe(5);

        (await store.ReleaseDeadNodeOwnershipAsync(Ct)).ShouldBe(2, "bounded: one batch of two per sweep");
        (await stillOwnedAsync(store, 77)).ShouldBe(3);

        (await store.ReleaseDeadNodeOwnershipAsync(Ct)).ShouldBe(2, "progress: the number is still confirmed dead, the next sweep releases the next batch");
        (await store.ReleaseDeadNodeOwnershipAsync(Ct)).ShouldBe(1);
        (await stillOwnedAsync(store, 77)).ShouldBe(0);
        (await store.ReleaseDeadNodeOwnershipAsync(Ct)).ShouldBe(0, "nothing left to release");
    }

    [Fact]
    public async Task max_batches_per_cycle_bounds_the_work_across_both_collections()
    {
        await _fixture.ClearAll();
        var store = buildStore(batchSize: 3, maxBatches: 2);
        await seedOwnedByDeadNodeAsync(store, 78, incoming: 7, outgoing: 7);

        await store.ReleaseDeadNodeOwnershipAsync(Ct);
        var released = await store.ReleaseDeadNodeOwnershipAsync(Ct);

        // 2 batches × 3 per collection: 6 incoming + 6 outgoing.
        released.ShouldBe(12);
        (await stillOwnedAsync(store, 78)).ShouldBe(2);

        (await store.ReleaseDeadNodeOwnershipAsync(Ct)).ShouldBe(2);
        (await stillOwnedAsync(store, 78)).ShouldBe(0);
    }

    [Fact]
    public async Task a_non_positive_batch_size_releases_everything_in_one_unbounded_update()
    {
        await _fixture.ClearAll();
        var store = buildStore(batchSize: 0, maxBatches: 1);
        await seedOwnedByDeadNodeAsync(store, 79, incoming: 4, outgoing: 3);

        await store.ReleaseDeadNodeOwnershipAsync(Ct);
        (await store.ReleaseDeadNodeOwnershipAsync(Ct)).ShouldBe(7);
        (await stillOwnedAsync(store, 79)).ShouldBe(0);
    }

    [Fact]
    public async Task a_live_node_is_never_released_even_in_a_bounded_sweep()
    {
        await _fixture.ClearAll();
        var store = buildStore(batchSize: 1, maxBatches: 10);
        var live = new Runtime.Agents.WolverineNode { NodeId = Guid.NewGuid(), ControlUri = new Uri("dbcontrol://live") };
        var liveNumber = await store.Nodes.PersistAsync(live, Ct);
        await seedOwnedByDeadNodeAsync(store, liveNumber, incoming: 3, outgoing: 0);
        await seedOwnedByDeadNodeAsync(store, 80, incoming: 3, outgoing: 0);

        await store.ReleaseDeadNodeOwnershipAsync(Ct);
        (await store.ReleaseDeadNodeOwnershipAsync(Ct)).ShouldBe(3);

        (await stillOwnedAsync(store, liveNumber)).ShouldBe(3, "the release names dead numbers positively; a live node's work is untouched");
        (await stillOwnedAsync(store, 80)).ShouldBe(0);
    }

    [Fact]
    public async Task solo_mode_runs_no_sweep_and_balanced_mode_sweeps_on_its_own_polling_time()
    {
        await _fixture.ClearAll();

        using var solo = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.DurabilityAgentEnabled = false;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(Ct);
        new MongoDbDurabilityAgent(solo.GetRuntime(), _fixture.BuildMessageStore()).RunsOrphanSweep
            .ShouldBeFalse("Solo mode has no peers to orphan anything");

        using var balanced = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Durability.DurabilityAgentEnabled = false;
                opts.Durability.ScheduledJobFirstExecution = TimeSpan.Zero;
                opts.Durability.OrphanedMessageSweepPollingTime = 200.Milliseconds();
                opts.Transports.NodeControlEndpoint =
                    opts.Transports.GetOrCreateEndpoint(new Uri($"tcp://localhost:{PortFinder.GetAvailablePort()}"));
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(Ct);

        var store = _fixture.BuildMessageStore();
        await seedOwnedByDeadNodeAsync(store, 81, incoming: 3, outgoing: 2);

        var agent = new MongoDbDurabilityAgent(balanced.GetRuntime(), store);
        agent.RunsOrphanSweep.ShouldBeTrue();
        await agent.StartAsync(Ct);
        try
        {
            // Two sweeps (two-tick confirmation) at 200 ms must have released the backlog well within
            // the bound. Polling the real condition, not sleeping a fixed time.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            var remaining = int.MaxValue;
            while (DateTime.UtcNow < deadline)
            {
                remaining = await stillOwnedAsync(store, 81);
                if (remaining == 0) break;
                await Task.Delay(50, Ct);
            }

            remaining.ShouldBe(0, "the sweep loop must release the dead node's work on its own polling time");
        }
        finally
        {
            await agent.StopAsync(Ct);
        }
    }
}
