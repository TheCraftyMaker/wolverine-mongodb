using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests;
using Wolverine.ComplianceTests.Scheduling;
using Wolverine.Util;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class scheduled_job_compliance : ScheduledJobCompliance
{
    private readonly AppFixture _fixture;
    public scheduled_job_compliance(AppFixture fixture) => _fixture = fixture;

    public override void ConfigurePersistence(WolverineOptions opts)
    {
        opts.Durability.Mode = DurabilityMode.Solo;
        opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
        opts.UseMongoDbPersistence(AppFixture.DatabaseName);
        opts.Transports.NodeControlEndpoint =
            opts.Transports.GetOrCreateEndpoint(new Uri($"tcp://localhost:{PortFinder.GetAvailablePort()}"));
    }
}
