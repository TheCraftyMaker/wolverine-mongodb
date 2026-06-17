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
// (lease / heartbeat-period sweeps, RavenDb-style config) fixes it.
//
// DECISION (2026-06-16): keep this suite GATED — deliberately, not pending work. The provider
// keeps the production-appropriate "any healthy node leads" model rather than constraining
// production to satisfy this upstream test's lowest-node assertion, matching how Wolverine's
// own Cosmos provider gates the same facts [Flaky]. The "lowest live node wins" election that
// would make these facts pass was analyzed and DECLINED for production (it degrades real
// failover); it is documented as an upstream-parity option only. Production confidence for
// multinode comes from the cross-node message-guarantee tests (plan Task 7), which are
// leader-identity-independent — not from this leadership-identity suite.
//
// Full diagnosis, config matrix, interleavings, MassTransit comparison, and the documented
// (declined) code change:
//   docs/superpowers/plans/2026-06-16-task6-multinode-compliance-findings.md
// (decision recorded in FOLLOWUPS.md).
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
