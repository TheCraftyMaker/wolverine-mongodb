using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.MongoDB.Internals.Transport;

internal class MongoDbControlListener : IListener
{
    private readonly CancellationTokenSource _cancellation;
    private readonly IReceiver _receiver;
    private readonly MongoDbControlTransport _transport;
    private readonly RetryBlock<Envelope> _completeBlock;
    private readonly Task _receivingLoop;

    public MongoDbControlListener(MongoDbControlTransport transport, MongoDbControlEndpoint endpoint,
        IReceiver receiver, ILogger<MongoDbControlListener> logger, CancellationToken cancellation)
    {
        _transport = transport;
        _receiver = receiver;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Address = endpoint.Uri;

        _completeBlock = new RetryBlock<Envelope>(deleteAsync, logger, cancellation);

        _receivingLoop = Task.Run(async () =>
        {
            await Task.Delay(Random.Shared.Next(100, 1000).Milliseconds(), _cancellation.Token);

            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await pollAsync();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error polling the MongoDB control queue for node {NodeId}",
                        transport.Options.UniqueNodeId);
                }

                await Task.Delay(1.Seconds(), _cancellation.Token);
            }
        }, _cancellation.Token);
    }

    public Uri Address { get; }
    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public async ValueTask CompleteAsync(Envelope envelope) => await _completeBlock.PostAsync(envelope);

    public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

    public async ValueTask StopAsync() => await _cancellation.CancelAsync();

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _receivingLoop.SafeDispose();
        _completeBlock.SafeDispose();
    }

    private async Task pollAsync()
    {
        var filter = Builders<ControlMessageDocument>.Filter.And(
            Builders<ControlMessageDocument>.Filter.Eq(x => x.NodeId, _transport.Options.UniqueNodeId),
            Builders<ControlMessageDocument>.Filter.Gt(x => x.Expires, DateTime.UtcNow));

        var documents = await _transport.Messages.Find(filter)
            .SortBy(x => x.Posted)
            .ToListAsync(_cancellation.Token);

        if (documents.Count == 0) return;

        var envelopes = documents.Select(d => EnvelopeSerializer.Deserialize(d.Body)).ToArray();

        await _receiver.ReceivedAsync(this, envelopes);
        await _transport.DeleteEnvelopesAsync(envelopes.ToList());
    }

    private Task deleteAsync(Envelope envelope, CancellationToken cancellation)
        => _transport.Messages.DeleteOneAsync(
            Builders<ControlMessageDocument>.Filter.Eq(x => x.Id, envelope.Id), cancellation);
}
