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

        var session = await host.TrackActivity().SendMessageAndWaitAsync(new RecordThing(1));
        session.Executed.SingleMessage<RecordThing>().Number.ShouldBe(1);
    }
}

public record RecordThing(int Number);
public static class RecordThingHandler
{
    public static void Handle(RecordThing _) { }
}
