using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Wolverine.ComplianceTests;
using Wolverine.Transports.Tcp;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class message_store_compliance : MessageStoreCompliance
{
    private readonly AppFixture _fixture;
    public message_store_compliance(AppFixture fixture) => _fixture = fixture;

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
