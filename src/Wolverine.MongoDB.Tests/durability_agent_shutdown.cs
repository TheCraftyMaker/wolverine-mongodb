using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;
using Wolverine.Tracking;

namespace Wolverine.MongoDB.Tests;

/// <summary>
/// <see cref="MongoDbDurabilityAgent.StopAsync"/> must await its recovery/scheduled-job loops
/// (bounded) before returning, so a caller that treats StopAsync's completion as "safe to tear
/// down the node" (NodeAgentController.stopAllAgentsAsync -> node-document delete) is not racing
/// a loop iteration that is still mid-flight.
/// </summary>
[Collection("mongodb")]
public class durability_agent_shutdown
{
    private readonly AppFixture _fixture;
    public durability_agent_shutdown(AppFixture fixture) => _fixture = fixture;

    private async Task<(IHost Host, MongoDbDurabilityAgent Agent)> StartAgent()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = TimeSpan.Zero;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync();

        var runtime = host.GetRuntime();
        var store = _fixture.BuildMessageStore();
        var agent = new MongoDbDurabilityAgent(runtime, store);
        await agent.StartAsync(CancellationToken.None);

        return (host, agent);
    }

    [Fact]
    public async Task stop_async_awaits_both_loops_before_returning()
    {
        await _fixture.ClearAll();
        var (host, agent) = await StartAgent();
        using var _ = host;

        await agent.StopAsync(CancellationToken.None);

        Assert.NotNull(agent.RecoveryTask);
        Assert.NotNull(agent.ScheduledJobTask);
        agent.RecoveryTask!.IsCompleted.ShouldBeTrue(
            "StopAsync must not return while the recovery loop is still running");
        agent.ScheduledJobTask!.IsCompleted.ShouldBeTrue(
            "StopAsync must not return while the scheduled-job loop is still running");
        agent.Status.ShouldBe(AgentStatus.Stopped);
    }

    [Fact]
    public async Task stop_async_disposes_the_cancellation_token_sources()
    {
        // Loop-completion timing races with how far a tick has gotten (PeriodicTimer's
        // cancellation resolves synchronously, but Task.Delay's does not), so disposal is
        // the deterministic signal that StopAsync actually tore its CTSes down instead of
        // just firing cancellation and returning.
        await _fixture.ClearAll();
        var (host, agent) = await StartAgent();
        using var _ = host;

        await agent.StopAsync(CancellationToken.None);

        Should.Throw<ObjectDisposedException>(() => agent.CancellationSource.Cancel());
    }

    [Fact]
    public async Task second_stop_async_is_idempotent()
    {
        await _fixture.ClearAll();
        var (host, agent) = await StartAgent();
        using var _ = host;

        await agent.StopAsync(CancellationToken.None);
        await agent.StopAsync(CancellationToken.None);

        agent.Status.ShouldBe(AgentStatus.Stopped);
    }
}
