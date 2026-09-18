using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.MongoDB.Tests;

[Trait("Category", "multinode")]
[Collection("mongodb")]
public class control_queue_tests : IAsyncLifetime
{
    private readonly AppFixture _fixture;
    private IHost _sender = null!;
    private IHost _receiver = null!;
    private Uri _receiverUri = null!;

    public control_queue_tests(AppFixture fixture) => _fixture = fixture;

    public async ValueTask InitializeAsync()
    {
        await _fixture.ClearAll();

        _sender = await StartNode("Sender");
        _receiver = await StartNode("Receiver");

        var nodeId = _receiver.GetRuntime().Options.UniqueNodeId;
        _receiverUri = new Uri($"mongocontrol://{nodeId}");
    }

    private Task<IHost> StartNode(string serviceName) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.ServiceName = serviceName;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Discovery.IncludeType(typeof(ControlQueueMessageHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _sender.StopAsync(TestContext.Current.CancellationToken);
        _sender.Dispose();
        await _receiver.StopAsync(TestContext.Current.CancellationToken);
        _receiver.Dispose();
    }

    [Fact]
    public void control_endpoint_is_wired_up_in_balanced_mode()
    {
        var endpoint = _sender.GetRuntime().Options.Transports.NodeControlEndpoint;
        endpoint.ShouldNotBeNull();
        endpoint.Uri.Scheme.ShouldBe("mongocontrol");
    }

    [Fact]
    public async Task send_message_from_one_to_another()
    {
        var tracked = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .ExecuteAndWaitAsync(m => m.EndpointFor(_receiverUri).SendAsync(new ControlCommand(10)));

        tracked.Sent.RecordsInOrder().Single(x => x.Envelope!.Message?.GetType() == typeof(ControlCommand))
            .ServiceName!.ShouldBe("Sender");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope!.Message?.GetType() == typeof(ControlCommand))
            .ServiceName!.ShouldBe("Receiver");
    }

    [Fact]
    public async Task request_reply_message_from_one_to_another()
    {
        var (tracked, result) = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(60.Seconds())
            .InvokeAndWaitAsync<ControlResult>(new ControlQuery(13), _receiverUri);

        result!.Number.ShouldBe(13);

        tracked.Sent.RecordsInOrder().Single(x => x.Envelope!.Message!.GetType() == typeof(ControlQuery))
            .ServiceName!.ShouldBe("Sender");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope!.Message!.GetType() == typeof(ControlQuery))
            .ServiceName!.ShouldBe("Receiver");
        tracked.Sent.RecordsInOrder().Single(x => x.Envelope!.Message!.GetType() == typeof(ControlResult))
            .ServiceName!.ShouldBe("Receiver");
        tracked.Received.RecordsInOrder().Single(x => x.Envelope!.Message!.GetType() == typeof(ControlResult))
            .ServiceName!.ShouldBe("Sender");
    }
}

public record ControlQuery(int Number);
public record ControlResult(int Number);
public record ControlCommand(int Number);

public static class ControlQueueMessageHandler
{
    public static ControlResult Handle(ControlQuery query) => new(query.Number);

    public static void Handle(ControlCommand command) => Debug.WriteLine($"Got command {command.Number}");

    public static void Handle(ControlResult result) => Debug.WriteLine($"Got result {result.Number}");
}
