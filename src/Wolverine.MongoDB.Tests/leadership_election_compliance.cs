using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests;
using Wolverine.Transports.Tcp;
using Xunit.Abstractions;

namespace Wolverine.MongoDB.Tests;

// Multi-node leadership election + agent balancing compliance, mirroring how the
// Postgres/SqlServer/RavenDb providers subclass LeadershipElectionCompliance.
// The lock lease is shortened so takeover/balancing assertions converge quickly.
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
