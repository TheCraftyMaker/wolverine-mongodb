using MongoDB.Driver;
using Wolverine.Persistence.Durability;
using Wolverine.Transports;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IMessageInbox
{
    public async Task StoreIncomingAsync(Envelope envelope)
    {
        var doc = new IncomingMessage(envelope);
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
        var docs = envelopes.Select(e => new IncomingMessage(e)).ToList();
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
            if (dupes.Count > 0) throw new DuplicateIncomingEnvelopeException(dupes);
        }
    }

    public async Task<bool> ExistsAsync(Envelope envelope, CancellationToken cancellation)
    {
        var id = new IncomingMessage(envelope).Id;
        return await Incoming.Find(Builders<IncomingMessage>.Filter.Eq(x => x.Id, id))
            .Limit(1).AnyAsync(cancellation);
    }

    public Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        var id = new IncomingMessage(envelope).Id;
        return Incoming.UpdateOneAsync(
            Builders<IncomingMessage>.Filter.Eq(x => x.Id, id),
            Builders<IncomingMessage>.Update
                .Set(x => x.Status, EnvelopeStatus.Handled)
                .Set(x => x.KeepUntil, DateTimeOffset.UtcNow.Add(_options.Durability.KeepAfterMessageHandling)));
    }

    public Task MarkIncomingEnvelopeAsHandledAsync(IReadOnlyList<Envelope> envelopes)
    {
        var ids = envelopes.Select(e => new IncomingMessage(e).Id).ToList();
        return Incoming.UpdateManyAsync(
            Builders<IncomingMessage>.Filter.In(x => x.Id, ids),
            Builders<IncomingMessage>.Update
                .Set(x => x.Status, EnvelopeStatus.Handled)
                .Set(x => x.KeepUntil, DateTimeOffset.UtcNow.Add(_options.Durability.KeepAfterMessageHandling)));
    }

    public Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        var id = new IncomingMessage(envelope).Id;
        return Incoming.UpdateOneAsync(
            Builders<IncomingMessage>.Filter.Eq(x => x.Id, id),
            Builders<IncomingMessage>.Update.Set(x => x.Attempts, envelope.Attempts));
    }

    public Task ScheduleExecutionAsync(Envelope envelope)
    {
        var id = new IncomingMessage(envelope).Id;
        return Incoming.UpdateOneAsync(
            Builders<IncomingMessage>.Filter.Eq(x => x.Id, id),
            Builders<IncomingMessage>.Update
                .Set(x => x.ExecutionTime, envelope.ScheduledTime)
                .Set(x => x.Status, EnvelopeStatus.Scheduled)
                .Set(x => x.Attempts, envelope.Attempts)
                .Set(x => x.OwnerId, 0));
    }

    public Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = TransportConstants.AnyNode;
        return StoreIncomingAsync(envelope);
    }

    public async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        var dlq = new DeadLetterMessage(envelope, exception)
        {
            ExpirationTime = envelope.DeliverBy ?? DateTimeOffset.UtcNow.Add(_options.Durability.DeadLetterQueueExpiration)
        };

        await DeadLetterDocs.ReplaceOneAsync(
            Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, dlq.Id),
            dlq, new ReplaceOptions { IsUpsert = true });

        var id = new IncomingMessage(envelope).Id;
        await Incoming.DeleteOneAsync(Builders<IncomingMessage>.Filter.Eq(x => x.Id, id));
    }

    public Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
        => Incoming.UpdateManyAsync(
            Builders<IncomingMessage>.Filter.And(
                Builders<IncomingMessage>.Filter.Eq(x => x.OwnerId, ownerId),
                Builders<IncomingMessage>.Filter.Eq(x => x.ReceivedAt, receivedAt.ToString())),
            Builders<IncomingMessage>.Update.Set(x => x.OwnerId, 0));
}
