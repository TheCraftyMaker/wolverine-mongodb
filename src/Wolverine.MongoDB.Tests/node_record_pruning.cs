using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// Node-record housekeeping (upstream GH-3701) applied by the MongoDB durability agent. Upstream keeps
/// this in the RDBMS <c>DurabilityAgent</c> only, so <c>NodeRecordRetention</c>, <c>NodeRecordPruningPeriod</c>
/// and <c>NodeEventRecordExpirationTime</c> were silently ignored here; RavenDb and CosmosDb ignore them
/// still. The pass itself is deterministic (one call = one pass); the loop's cadence is proven through
/// the agent's own start-delay rule and a live agent with a short period.
/// </summary>
[Collection("mongodb")]
public class node_record_pruning
{
    private readonly AppFixture _fixture;
    public node_record_pruning(AppFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeRecord record(int i, DateTimeOffset timestamp) => new()
    {
        NodeNumber = 1,
        RecordType = NodeRecordType.AssignmentChanged,
        Timestamp = timestamp,
        Description = $"record {i}",
        ServiceName = "pruning"
    };

    [Fact]
    public async Task a_pass_deletes_records_older_than_the_expiration_and_trims_to_the_retention()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();
        var settings = new DurabilitySettings
        {
            NodeEventRecordExpirationTime = 1.Hours(),
            NodeRecordRetention = 3
        };

        var now = DateTimeOffset.UtcNow;
        // 4 expired (older than an hour), 6 fresh (the last minute).
        await store.Nodes.LogRecordsAsync(Enumerable.Range(0, 4).Select(i => record(i, now.AddHours(-2).AddSeconds(i))).ToArray());
        await store.Nodes.LogRecordsAsync(Enumerable.Range(10, 6).Select(i => record(i, now.AddSeconds(-60 + i))).ToArray());

        var expired = await store.PruneNodeRecordsAsync(settings, Ct);

        expired.ShouldBe(4, "the age bound removes exactly the expired records");
        var remaining = await store.Nodes.FetchRecentRecordsAsync(100);
        remaining.Count.ShouldBe(3, "the row cap then trims what the age bound left to the retention");
        remaining.Select(x => x.Description).ShouldBe(["record 13", "record 14", "record 15"]);
    }

    [Fact]
    public async Task a_non_positive_retention_applies_only_the_age_bound()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();
        var settings = new DurabilitySettings { NodeEventRecordExpirationTime = 1.Hours(), NodeRecordRetention = 0 };

        var now = DateTimeOffset.UtcNow;
        await store.Nodes.LogRecordsAsync(record(0, now.AddDays(-1)), record(1, now), record(2, now), record(3, now));

        (await store.PruneNodeRecordsAsync(settings, Ct)).ShouldBe(1);
        (await store.Nodes.FetchRecentRecordsAsync(100)).Count.ShouldBe(3, "no cap is applied when the retention is off");
    }

    [Fact]
    public async Task a_pass_on_an_empty_collection_is_a_no_op()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();
        (await store.PruneNodeRecordsAsync(new DurabilitySettings(), Ct)).ShouldBe(0);
    }

    [Fact]
    public void the_first_pass_is_held_back_at_most_one_minute_but_never_past_the_period()
    {
        MongoDbDurabilityAgent.PruningStartDelay(new DurabilitySettings { NodeRecordPruningPeriod = 1.Hours() })
            .ShouldBe(1.Minutes());
        MongoDbDurabilityAgent.PruningStartDelay(new DurabilitySettings { NodeRecordPruningPeriod = 200.Milliseconds() })
            .ShouldBe(200.Milliseconds());
    }

    private async Task<(IHost Host, MongoDbMessageStore Store, MongoDbDurabilityAgent Agent)> startAgentAsync(
        Action<DurabilitySettings> configure, MessageStoreRole role = MessageStoreRole.Main)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = TimeSpan.Zero;
                // Keep the host's own durability agent quiet on this database: the agent under test is
                // the one constructed below.
                opts.Durability.DurabilityAgentEnabled = false;
                configure(opts.Durability);
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(Ct);

        var store = _fixture.BuildMessageStore();
        store.Role = role;
        var agent = new MongoDbDurabilityAgent(host.GetRuntime(), store);
        return (host, store, agent);
    }

    [Fact]
    public async Task the_agent_prunes_on_the_configured_period_for_the_main_store()
    {
        await _fixture.ClearAll();
        var (host, store, agent) = await startAgentAsync(d =>
        {
            d.NodeRecordPruningPeriod = 200.Milliseconds();
            d.NodeEventRecordExpirationTime = 1.Hours();
            d.NodeRecordRetention = 2;
        });
        using var _ = host;

        var now = DateTimeOffset.UtcNow;
        await store.Nodes.LogRecordsAsync(record(0, now.AddDays(-1)), record(1, now.AddSeconds(-3)), record(2, now.AddSeconds(-2)), record(3, now.AddSeconds(-1)));

        agent.PrunesNodeRecords.ShouldBeTrue();
        await agent.StartAsync(Ct);
        try
        {
            // Bounded wait: a 200 ms period must have pruned within a few seconds. Polling a real
            // condition rather than sleeping a fixed time.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            IReadOnlyList<NodeRecord> remaining;
            do
            {
                remaining = await store.Nodes.FetchRecentRecordsAsync(100);
                if (remaining.Count == 2) break;
                await Task.Delay(50, Ct);
            } while (DateTime.UtcNow < deadline);

            // The host writes its own node-event records at start-up, so the two survivors are simply the
            // newest two: the seeded oldest ones must be gone.
            remaining.Count.ShouldBe(2, "the agent's pruning loop must apply the retention on its own period");
            remaining.Select(x => x.Description).ShouldNotContain("record 0");
            remaining.Select(x => x.Description).ShouldNotContain("record 1");
        }
        finally
        {
            await agent.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task an_ancillary_store_or_a_disabled_period_runs_no_pruning_loop()
    {
        await _fixture.ClearAll();

        var (host1, _, ancillary) = await startAgentAsync(_ => { }, MessageStoreRole.Ancillary);
        using (host1)
        {
            ancillary.PrunesNodeRecords.ShouldBeFalse("node records exist on the Main store only");
        }

        var (host2, _, disabled) = await startAgentAsync(d => d.NodeRecordPruningPeriod = TimeSpan.Zero);
        using (host2)
        {
            disabled.PrunesNodeRecords.ShouldBeFalse("a non-positive period switches the loop off");
        }
    }
}
