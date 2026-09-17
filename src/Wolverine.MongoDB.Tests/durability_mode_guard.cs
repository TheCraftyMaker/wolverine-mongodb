using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class durability_mode_guard
{
    private readonly AppFixture _fixture;
    public durability_mode_guard(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task balanced_mode_starts_with_a_control_endpoint()
    {
        await _fixture.ClearAll();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                // MongoDB has no native control transport; Balanced requires one.
                opts.UseTcpForControlEndpoint();
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(TestContext.Current.CancellationToken);
        host.ShouldNotBeNull();
    }

    [Fact]
    public async Task solo_mode_starts_normally()
    {
        await _fixture.ClearAll();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(TestContext.Current.CancellationToken);
        host.ShouldNotBeNull();
    }

    [Fact]
    public async Task balanced_mode_starts_without_an_explicit_control_endpoint()
    {
        await _fixture.ClearAll();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(TestContext.Current.CancellationToken);

        var endpoint = host.GetRuntime().Options.Transports.NodeControlEndpoint;
        endpoint.ShouldNotBeNull();
        endpoint.Uri.Scheme.ShouldBe("mongocontrol");
        endpoint.Uri.Host.ShouldBe(host.GetRuntime().Options.UniqueNodeId.ToString());
    }

    [Fact]
    public async Task an_explicitly_configured_control_endpoint_is_kept()
    {
        await _fixture.ClearAll();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.UseTcpForControlEndpoint();
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(TestContext.Current.CancellationToken);

        host.GetRuntime().Options.Transports.NodeControlEndpoint!.Uri.Scheme.ShouldBe("tcp");
    }
}
