using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.MongoDB.Internals.Transport;

internal class MongoDbControlEndpoint : Endpoint
{
    private readonly MongoDbControlTransport _parent;

    public MongoDbControlEndpoint(MongoDbControlTransport parent, Guid nodeId)
        : base(new Uri($"{MongoDbControlTransport.ProtocolName}://{nodeId}"), EndpointRole.System)
    {
        _parent = parent;
        NodeId = nodeId;
        Mode = EndpointMode.BufferedInMemory;
        MaxDegreeOfParallelism = 1;
        BrokerRole = "queue";

        TelemetryEnabled = false;
    }

    public Guid NodeId { get; }

    protected override bool supportsMode(EndpointMode mode) => mode == EndpointMode.BufferedInMemory;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
        => new(new MongoDbControlListener(_parent, this, receiver,
            runtime.LoggerFactory.CreateLogger<MongoDbControlListener>(), runtime.Options.Durability.Cancellation));

    protected override ISender CreateSender(IWolverineRuntime runtime)
        => new MongoDbControlSender(this, _parent, runtime.LoggerFactory.CreateLogger<MongoDbControlSender>(),
            runtime.Options.Durability.Cancellation);
}
