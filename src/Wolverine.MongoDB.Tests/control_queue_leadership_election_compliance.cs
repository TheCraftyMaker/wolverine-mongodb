using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests;

namespace Wolverine.MongoDB.Tests;

// The upstream leadership battery over the native mongocontrol transport rather than TCP, the way
// RavenDb runs its copy twice. It covers cross-node agent convergence on the transport a
// Balanced-mode consumer actually gets, including
// every_agent_runs_exactly_once_after_the_leader_dies_with_its_starts_in_flight.
// CI excludes singular_agent_is_only_running_on_one from this class by name: that fact asserts
// exclusivity the instant any node reports the agent, with no settling time, so it samples inside
// the poll-interval handoff window. leadership_election_compliance keeps it gated on TCP; the
// reason sits next to the filter in .github/workflows/ci.yml.
// [Collection("mongodb")] stops this class from running its cluster against the same database as
// the TCP class at the same time.
[Trait("Category", "multinode")]
[Collection("mongodb")]
public class control_queue_leadership_election_compliance : LeadershipElectionCompliance
{
    private readonly AppFixture _fixture;

    public control_queue_leadership_election_compliance(AppFixture fixture, ITestOutputHelper output) : base(output)
    {
        _fixture = fixture;
    }

    protected override void configureNode(WolverineOptions opts)
    {
        // No control endpoint on purpose: MongoDbMessageStore.Initialize then registers mongocontrol
        // and sets it as the node control endpoint, so leadership handoff and agent assignment in
        // this suite go through wolverine_control_messages.
        opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
        opts.UseMongoDbPersistence(AppFixture.DatabaseName);
    }

    protected override Task beforeBuildingHost()
    {
        return _fixture.ClearAll();
    }
}
