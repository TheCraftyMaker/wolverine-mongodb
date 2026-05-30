using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Wolverine.ComplianceTests;
using Wolverine.Transports.Tcp;
using Xunit.Abstractions;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class dead_letter_admin_compliance : DeadLetterAdminCompliance
{
    private readonly AppFixture _fixture;

    public dead_letter_admin_compliance(AppFixture fixture, ITestOutputHelper output) : base(output)
    {
        _fixture = fixture;
    }

    public override async Task<IHost> BuildCleanHost()
    {
        await _fixture.ClearAll();
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.ListenAtPort(2345).UseDurableInbox();
            }).StartAsync();
    }
}
