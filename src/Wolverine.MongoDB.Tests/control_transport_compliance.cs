using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.MongoDB.Internals.Transport;
using Wolverine.Runtime;

namespace Wolverine.MongoDB.Tests;

// The upstream transport contract (send by destination, request/reply, listener stop/restart,
// correlation, scheduling) run over the mongocontrol transport. Same shape as
// RavenDbTests/control_transport_compliance.cs.
public class MongoDbControlTransportFixture : TransportComplianceFixture, IAsyncLifetime
{
    private readonly AppFixture _mongo = new();

    public MongoDbControlTransportFixture() : base(new Uri("mongocontrol://placeholder"), 30)
    {
        Mode = DurabilityMode.Balanced;
        MustReset = false;
    }

    public async ValueTask InitializeAsync()
    {
        await _mongo.InitializeAsync();
        await _mongo.ClearAll();

        await ReceiverIs(opts =>
        {
            opts.Services.AddSingleton<IMongoClient>(_mongo.Client);
            opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            tightenClusterCadence(opts);
        });

        var receiverNodeId = Receiver.Services.GetRequiredService<IWolverineRuntime>().Options.UniqueNodeId;
        OutboundAddress = new Uri($"mongocontrol://{receiverNodeId}");

        await SenderIs(opts =>
        {
            opts.Services.AddSingleton<IMongoClient>(_mongo.Client);
            opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            tightenClusterCadence(opts);

            // The store only registers its control transport lazily, inside Initialize, which
            // runs during IHostedService.StartAsync -- after IHostBuilder.Build() has already
            // resolved the compliance harness's PublishAllMessages().To(OutboundAddress) rule.
            // A real consumer never needs this: nothing in production publishes application
            // messages to a control queue, so nothing else resolves the scheme before Initialize
            // runs. Only this harness does, so only this harness registers the transport early.
            // MongoDbMessageStore.Initialize finds this instance already registered and reuses
            // it rather than constructing a second one.
            opts.Transports.Add(new MongoDbControlTransport(_mongo.Client.GetDatabase(AppFixture.DatabaseName), opts));
        });
    }

    private static void tightenClusterCadence(WolverineOptions opts)
    {
        opts.Durability.CheckAssignmentPeriod = 1.Seconds();
        opts.Durability.HealthCheckPollingTime = 1.Seconds();
        opts.Durability.ScheduledJobPollingTime = 1.Seconds();
        opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
    }

    public new async ValueTask DisposeAsync() => await ValueTask.CompletedTask;
}

[Trait("Category", "multinode")]
[Collection("mongodb")]
public class control_transport_compliance : TransportCompliance<MongoDbControlTransportFixture>;
