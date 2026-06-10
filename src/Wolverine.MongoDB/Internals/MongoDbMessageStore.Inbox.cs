using MongoDB.Driver;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IMessageInbox
{
    public async Task StoreIncomingAsync(Envelope envelope)
    {
        var doc = new IncomingMessage(envelope, InboxIdentity(envelope));
        try
        {
            await Incoming.InsertOneAsync(doc);
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            throw new DuplicateIncomingEnvelopeException(envelope);
        }
    }

    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        if (envelopes.Count == 0) return;
        var docs = envelopes.Select(e => new IncomingMessage(e, InboxIdentity(e))).ToList();
        try
        {
            await Incoming.InsertManyAsync(docs, new InsertManyOptions { IsOrdered = false });
        }
        catch (MongoBulkWriteException<IncomingMessage> ex)
        {
            var dupes = ex.WriteErrors
                .Where(w => w.Category == ServerErrorCategory.DuplicateKey)
                .Select(w => envelopes[w.Index])
                .ToList();
            var others = ex.WriteErrors
                .Where(w => w.Category != ServerErrorCategory.DuplicateKey)
                .ToList();

            // Non-duplicate failures must surface — silently swallowing them would lose messages.
            if (others.Count > 0) throw;
            if (dupes.Count > 0) throw new DuplicateIncomingEnvelopeException(dupes);
        }
    }

    public async Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        var id = InboxIdentity(envelope);
        return await Incoming.Find(Builders<IncomingMessage>.Filter.Eq(x => x.Id, id))
            .Limit(1).AnyAsync(cancellation);
    }

    /// <summary>
    /// Existence check scoped to an active session/transaction. Used by the eager
    /// idempotency check so that a duplicate is detected via a READ rather than a
    /// duplicate-key INSERT — a failed insert inside a Mongo transaction aborts the
    /// whole transaction, stranding the subsequent outgoing-message writes.
    /// </summary>
    internal Task<bool> ExistsAsync(IClientSessionHandle session, Envelope envelope, CancellationToken cancellation)
    {
        var id = InboxIdentity(envelope);
        return Incoming.Find(session, Builders<IncomingMessage>.Filter.Eq(x => x.Id, id))
            .Limit(1).AnyAsync(cancellation);
    }

    public Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        var id = InboxIdentity(envelope);
        return Incoming.UpdateOneAsync(
            Builders<IncomingMessage>.Filter.Eq(x => x.Id, id),
            Builders<IncomingMessage>.Update
                .Set(x => x.Status, EnvelopeStatus.Handled)
                .Set(x => x.KeepUntil, DateTimeOffset.UtcNow.Add(_options.Durability.KeepAfterMessageHandling)));
    }

    public Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        var ids = envelopes.Select(InboxIdentity).ToList();
        return Incoming.UpdateManyAsync(
            Builders<IncomingMessage>.Filter.In(x => x.Id, ids),
            Builders<IncomingMessage>.Update
                .Set(x => x.Status, EnvelopeStatus.Handled)
                .Set(x => x.KeepUntil, DateTimeOffset.UtcNow.Add(_options.Durability.KeepAfterMessageHandling)));
    }

    public Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        var id = InboxIdentity(envelope);
        return Incoming.UpdateOneAsync(
            Builders<IncomingMessage>.Filter.Eq(x => x.Id, id),
            Builders<IncomingMessage>.Update.Set(x => x.Attempts, envelope.Attempts));
    }

    public Task ScheduleExecutionAsync(Envelope envelope)
    {
        var id = InboxIdentity(envelope);
        return Incoming.UpdateOneAsync(
            Builders<IncomingMessage>.Filter.Eq(x => x.Id, id),
            Builders<IncomingMessage>.Update
                .Set(x => x.ExecutionTime, envelope.ScheduledTime?.ToUniversalTime())
                .Set(x => x.Status, EnvelopeStatus.Scheduled)
                .Set(x => x.Attempts, envelope.Attempts)
                .Set(x => x.OwnerId, 0));
    }

    public async Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;
        var id = InboxIdentity(envelope);
        var result = await Incoming.UpdateOneAsync(
            Builders<IncomingMessage>.Filter.Eq(x => x.Id, id),
            Builders<IncomingMessage>.Update
                .Set(x => x.Status, EnvelopeStatus.Scheduled)
                .Set(x => x.OwnerId, 0)
                .Set(x => x.ExecutionTime, envelope.ScheduledTime?.ToUniversalTime())
                .Set(x => x.Attempts, envelope.Attempts));
        if (result.MatchedCount == 0)
        {
            await StoreIncomingAsync(envelope);
        }
    }

    public async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        // Guard body serialization: a poison message whose envelope fails to serialize must
        // still leave the inbox. Build the DLQ doc with a safe/empty body in that case rather
        // than letting the move throw and strand the message in incoming forever.
        DeadLetterMessage dlq;
        try
        {
            dlq = new DeadLetterMessage(envelope, exception);
        }
        catch (Exception serializeFailure)
        {
            dlq = DeadLetterMessage.ForUnserializableEnvelope(envelope, exception, serializeFailure);
        }

        // Wolverine semantics: dead letters are retained forever unless the application
        // explicitly opts into expiration. The TTL index skips documents without the field.
        if (_options.Durability.DeadLetterQueueExpirationEnabled)
        {
            dlq.ExpirationTime = envelope.DeliverBy ??
                                 DateTimeOffset.UtcNow.Add(_options.Durability.DeadLetterQueueExpiration);
        }

        var id = InboxIdentity(envelope);

        // Wrap the DLQ upsert and incoming delete in a single replica-set transaction so a crash
        // between them cannot duplicate the dead letter or strand the incoming envelope.
        // WithTransactionAsync transparently retries TransientTransactionError /
        // UnknownTransactionCommitResult (e.g. write conflicts under concurrency), and aborts
        // automatically if the body throws.
        using var session = await _client.StartSessionAsync();
        await session.WithTransactionAsync(async (s, ct) =>
        {
            await DeadLetterDocs.ReplaceOneAsync(s,
                Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, dlq.Id),
                dlq, new ReplaceOptions { IsUpsert = true }, ct);

            await Incoming.DeleteOneAsync(s, Builders<IncomingMessage>.Filter.Eq(x => x.Id, id),
                cancellationToken: ct);

            return true;
        });
    }

    public Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
        => Incoming.UpdateManyAsync(
            Builders<IncomingMessage>.Filter.And(
                Builders<IncomingMessage>.Filter.Eq(x => x.OwnerId, ownerId),
                Builders<IncomingMessage>.Filter.Eq(x => x.ReceivedAt, receivedAt.ToString())),
            Builders<IncomingMessage>.Update.Set(x => x.OwnerId, 0));
}
