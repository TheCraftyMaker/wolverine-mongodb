using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class end_to_end
{
    private readonly AppFixture _fixture;
    public end_to_end(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task publish_locally_with_durable_inbox()
    {
        await _fixture.ClearAll();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.LocalQueue("things").UseDurableInbox();
            }).StartAsync();

        // Explicit timeout: the default TrackActivity window is short, and when both TFMs run
        // concurrently (two MongoDB containers contending) the durable-inbox round-trip can exceed it.
        var session = await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .SendMessageAndWaitAsync(new RecordThing(1));
        session.Executed.SingleMessage<RecordThing>().Number.ShouldBe(1);
    }
}

public record RecordThing(int Number);
public static class RecordThingHandler
{
    public static void Handle(RecordThing _) { }
}
