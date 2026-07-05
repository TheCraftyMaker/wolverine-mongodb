using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests;
using Wolverine.Transports.Tcp;
using Xunit.Abstractions;

namespace Wolverine.MongoDB.Tests;

// MULTI-NODE LEADERSHIP-ELECTION COMPLIANCE — the 13 upstream LeadershipElectionCompliance facts.
//
// History: Task 6 of the multinode plan (against WolverineFx 6.2.2) could NOT reach five
// consecutive green runs. `leader_switchover_between_nodes` and the dependent
// `singular_agent_is_only_running_on_one` hinged on a non-deterministic leadership-claim race
// that core decided by lock-arrival order, which our durable (w:majority+j:true) Mongo lock lost
// ~half the time. The suite was therefore compile-gated behind `#if RUN_MULTINODE`, matching how
// Wolverine's own Cosmos provider gates the same facts [Flaky].
//   Full diagnosis: docs/superpowers/plans/2026-06-16-task6-multinode-compliance-findings.md
//
// UN-GATED (2026-07-05, plan T4.5): WolverineFx 6.9.0 reworked these facts —
// `leader_switchover_between_nodes` now uses a slow heartbeat plus an explicit `CheckAgentHealth`
// trigger (removing the lock-arrival-order race), and the new
// `take_over_leader_ship_if_leader_becomes_stale_with_racing_nodes` fact is built around the
// "any healthy node leads" model this provider already implements — so un-gating did NOT require
// the declined "lowest live node wins" election change. Verified 5x consecutive green on BOTH
// net9.0 and net10.0 (10/10 runs of the full Category=multinode suite) before removing the guard.
// The [Trait("Category","multinode")] below routes it into CI's existing multinode step with no
// ci.yml change (that step already runs `dotnet test --filter "Category=multinode"`).
//   Decision + 5x proof: FOLLOWUPS.md and the multinode-leadership-model-decision memory.
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
