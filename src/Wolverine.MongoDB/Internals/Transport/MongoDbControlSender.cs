using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Wolverine.Transports.Sending;

namespace Wolverine.MongoDB.Internals.Transport;

internal class MongoDbControlSender : ISender, IAsyncDisposable
{
    private readonly MongoDbControlEndpoint _endpoint;
    private readonly MongoDbControlTransport _transport;
    private readonly RetryBlock<Envelope> _retryBlock;

    public MongoDbControlSender(MongoDbControlEndpoint endpoint, MongoDbControlTransport transport,
        ILogger logger, CancellationToken cancellation)
    {
        _endpoint = endpoint;
        _transport = transport;
        Destination = endpoint.Uri;
        _retryBlock = new RetryBlock<Envelope>(sendAsync, logger, cancellation);
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }

    public async Task<bool> PingAsync()
    {
        try
        {
            await _transport.Messages.Database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        envelope.DeliverWithin = 10.Seconds();
        await _retryBlock.PostAsync(envelope);
    }

    private async Task sendAsync(Envelope envelope, CancellationToken cancellation)
    {
        if (cancellation.IsCancellationRequested) return;

        var document = ControlMessageDocument.For(envelope, _endpoint.NodeId, DateTime.UtcNow.AddSeconds(30));

        try
        {
            await _transport.Messages.InsertOneAsync(document, cancellationToken: cancellation);
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _retryBlock.DrainAsync();
        _retryBlock.Dispose();
    }
}
