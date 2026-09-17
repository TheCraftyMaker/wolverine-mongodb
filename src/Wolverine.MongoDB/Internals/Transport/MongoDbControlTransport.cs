using JasperFx.Blocks;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Transports;

namespace Wolverine.MongoDB.Internals.Transport;

internal class MongoDbControlTransport : ITransport, IAsyncDisposable
{
    public const string ProtocolName = "mongocontrol";

    private readonly Cache<Guid, MongoDbControlEndpoint> _endpoints;
    private RetryBlock<List<Envelope>>? _deleteBlock;

    public MongoDbControlTransport(IMongoDatabase database, WolverineOptions options)
    {
        Options = options;
        Messages = database.GetCollection<ControlMessageDocument>(MongoConstants.ControlMessagesCollection);
        _endpoints = new Cache<Guid, MongoDbControlEndpoint>(nodeId => new MongoDbControlEndpoint(this, nodeId));
        ControlEndpoint = _endpoints[options.UniqueNodeId];
    }

    public WolverineOptions Options { get; }
    internal IMongoCollection<ControlMessageDocument> Messages { get; }
    public MongoDbControlEndpoint ControlEndpoint { get; }

    public string Protocol => ProtocolName;
    public string Name => "MongoDB control message transport for Wolverine node coordination";

    public Endpoint ReplyEndpoint() => ControlEndpoint;

    public Endpoint GetOrCreateEndpoint(Uri uri) => _endpoints[Guid.Parse(uri.Host)];

    public Endpoint? TryGetEndpoint(Uri uri)
        => _endpoints.TryFind(Guid.Parse(uri.Host), out var endpoint) ? endpoint : null;

    public IEnumerable<Endpoint> Endpoints() => _endpoints;

    public ValueTask InitializeAsync(IWolverineRuntime runtime)
    {
        foreach (var endpoint in Endpoints()) endpoint.Compile(runtime);

        _deleteBlock = new RetryBlock<List<Envelope>>(deleteAsync,
            runtime.LoggerFactory.CreateLogger<MongoDbControlTransport>(), runtime.Options.Durability.Cancellation);
        return ValueTask.CompletedTask;
    }

    public bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = default!;
        return false;
    }

    public bool TryBuildStatefulResource(IWolverineRuntime runtime, out IStatefulResource? resource)
    {
        resource = default;
        return false;
    }

    public Task DeleteEnvelopesAsync(List<Envelope> envelopes)
        => _deleteBlock?.PostAsync(envelopes)
           ?? throw new InvalidOperationException("The MongoDbControlTransport has not been initialized");

    private Task deleteAsync(List<Envelope> envelopes, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested || envelopes.Count == 0) return Task.CompletedTask;

        var ids = envelopes.Select(x => x.Id);
        return Messages.DeleteManyAsync(Builders<ControlMessageDocument>.Filter.In(x => x.Id, ids), cancellation);
    }

    public async ValueTask DisposeAsync()
    {
        if (_deleteBlock == null) return;

        try
        {
            await _deleteBlock.DrainAsync();
        }
        catch (TaskCanceledException)
        {
        }

        _deleteBlock.SafeDispose();
    }
}
