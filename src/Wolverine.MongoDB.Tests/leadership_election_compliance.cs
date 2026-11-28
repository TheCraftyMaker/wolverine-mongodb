using Bobcat;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests;
using Wolverine.Transports.Tcp;

namespace Wolverine.MongoDB.Tests;

// The upstream LeadershipElectionCompliance battery over an explicit TCP control endpoint. Any healthy node can lead, which is
// the model this provider implements, so the facts do not depend on which node wins a leadership claim.
// The multinode trait routes the class into CI's multinode step, which selects it with
// `dotnet test --filter-trait "Category=multinode"`. control_queue_leadership_election_compliance runs the same battery over the
// native mongocontrol transport.
[BobcatFeature("MongoDB leader election, TCP control endpoint")]
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
