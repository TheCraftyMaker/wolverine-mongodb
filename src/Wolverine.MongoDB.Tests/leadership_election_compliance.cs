#if RUN_MULTINODE
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests;
using Wolverine.Transports.Tcp;
using Xunit.Abstractions;

namespace Wolverine.MongoDB.Tests;

// MULTI-NODE LEADERSHIP-ELECTION COMPLIANCE — still compile-gated behind RUN_MULTINODE.
//
// Task 6 of the multinode plan attempted to un-gate this suite. 9–10 of the 12 facts pass
// reliably, but `leader_switchover_between_nodes` (and the dependent
// `singular_agent_is_only_running_on_one`) cannot reach five consecutive green runs: they hinge
// on a non-deterministic leadership-claim race that Wolverine core decides by lock-arrival order,
// which our durable (w:majority+j:true) Mongo lock loses ~half the time. No test-config knob
// (lease / heartbeat-period sweeps, RavenDb-style config) fixes it; the real fix is a
// library-level deterministic "lowest live node wins" election in
// MongoDbMessageStore.Locking.TryAttainLeadershipLockAsync.
//
// Full diagnosis, the config matrix, observed interleavings, a MassTransit comparison, the
// suggested code change, and model guidance:
//   docs/superpowers/plans/2026-06-16-task6-multinode-compliance-findings.md
// Tracked in FOLLOWUPS.md. Keep this gated until that fix lands; then drop the gate, keep the
// [Trait("Category","multinode")] below, and wire the separate CI category step (plan Task 8).
//
// To work the suite locally: dotnet test -p:DefineConstants=RUN_MULTINODE
[Trait("Category", "multinode")]
[Collection("mongodb")]
public class leadership_election_compliance : LeadershipElectionCompliance
{
    private readonly AppFixture _fixture;

    public leadership_election_compliance(AppFixture fixture, ITestOutputHelper output) : base(output)
    {
        _fixture = fixture;
    }

    protected override void configureNode(WolverineOptions opts)
    {
        // MongoDB has no native control transport (unlike Cosmos); use TCP for the
        // inter-node control endpoint required by Balanced mode, like RavenDb does.
        opts.UseTcpForControlEndpoint();

        opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
        opts.UseMongoDbPersistence(AppFixture.DatabaseName,
            mongo => mongo.LockLeaseDuration = TimeSpan.FromSeconds(5));
    }

    protected override Task beforeBuildingHost()
    {
        return _fixture.ClearAll();
    }
}
#endif
