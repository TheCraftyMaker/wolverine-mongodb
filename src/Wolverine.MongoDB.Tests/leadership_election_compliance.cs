using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests;
using Wolverine.Transports.Tcp;

namespace Wolverine.MongoDB.Tests;

// MULTI-NODE LEADERSHIP-ELECTION COMPLIANCE — the upstream LeadershipElectionCompliance facts.
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
// net9.0 and net10.0 (10/10 runs of the full Category=multinode suite) before removing the guard,
// on the TCP control endpoint this suite still configures. The suite carries 19 facts at the
// V6.38.0 pin; it carried 13 when it was un-gated.
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
        // This suite keeps an explicit TCP control endpoint. Besides durability_mode_guard, it is the
        // only place in the test assembly that does. singular_agent_is_only_running_on_one
        // asserts exclusivity the instant any node reports the agent, with no settling time, and it
        // is the only coverage anywhere of a SingularAgent running on at most one node: simple://
        // appears in no other fact, and the expectExactlyOneCopyOfEachAsync helpers are only ever
        // pointed at the twelve fake:// agents. Agent handoff over mongocontrol costs up to a poll
        // interval, which widens the window where a new leader re-drives a start the previous leader
        // already dispatched, so that assertion samples inside it. Upstream splits the same way:
        // RavenDbTests.LeaderElection/leadership_election_compliance.cs configures TCP, and a
        // separate control_queue_leadership_election_compliance runs the battery over the native
        // queue. control_transport_compliance and control_queue_tests cover the mongocontrol
        // transport's own contract.
        opts.UseTcpForControlEndpoint();

        opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
        opts.UseMongoDbPersistence(AppFixture.DatabaseName);
    }

    protected override Task beforeBuildingHost()
    {
        return _fixture.ClearAll();
    }
}
