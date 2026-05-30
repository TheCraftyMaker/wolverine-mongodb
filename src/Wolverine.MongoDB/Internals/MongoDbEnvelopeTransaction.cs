using MongoDB.Driver;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.MongoDB.Internals;

public class MongoDbEnvelopeTransaction : IEnvelopeTransaction
{
    private readonly IClientSessionHandle _session;
    private readonly MongoDbMessageStore _store;

    public MongoDbEnvelopeTransaction(IClientSessionHandle session, MessageContext context)
    {
        if (context.Storage is not MongoDbMessageStore store)
        {
            throw new InvalidOperationException(
                "This Wolverine application is not using MongoDB as the backing message persistence");
        }

        _session = session;
        _store = store;
    }

    public Task PersistOutgoingAsync(Envelope envelope)
    {
        return _store.Outgoing.InsertOneAsync(_session, new OutgoingMessage(envelope));
    }

    public Task PersistOutgoingAsync(Envelope[] envelopes)
    {
        return envelopes.Length == 0
            ? Task.CompletedTask
            : _store.Outgoing.InsertManyAsync(_session, envelopes.Select(e => new OutgoingMessage(e)));
    }

    public Task PersistIncomingAsync(Envelope envelope)
    {
        return _store.Incoming.InsertOneAsync(_session, new IncomingMessage(envelope));
    }

    public async Task<bool> TryMakeEagerIdempotencyCheckAsync(Envelope envelope, DurabilitySettings settings,
        CancellationToken cancellation)
    {
        var copy = Envelope.ForPersistedHandled(envelope, DateTimeOffset.UtcNow, settings);
        try
        {
            await PersistIncomingAsync(copy);
            envelope.WasPersistedInInbox = true;
            envelope.Status = EnvelopeStatus.Handled;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public ValueTask RollbackAsync()
    {
        if (_session.IsInTransaction)
        {
            _session.AbortTransaction();
        }

        return ValueTask.CompletedTask;
    }
}
