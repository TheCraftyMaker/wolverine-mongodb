using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class durability_mode_guard
{
    private readonly AppFixture _fixture;
    public durability_mode_guard(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task balanced_mode_fails_fast_at_startup()
    {
        await _fixture.ClearAll();

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // Balanced is Wolverine's DEFAULT mode — not setting Solo must fail loudly,
                    // not run a subtly broken cluster.
                    opts.Durability.Mode = DurabilityMode.Balanced;
                    opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                    opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                }).StartAsync();
        });

        ex.Message.ShouldContain("DurabilityMode.Solo");
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
            }).StartAsync();
        host.ShouldNotBeNull();
    }
}
