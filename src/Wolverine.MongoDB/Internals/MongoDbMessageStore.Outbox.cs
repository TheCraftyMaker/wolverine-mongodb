using MongoDB.Driver;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Internals;

public partial class MongoDbMessageStore : IMessageOutbox
{
    public async Task<IReadOnlyList<Envelope>> LoadOutgoingAsync(Uri destination)
    {
        // Mirrors the RDBMS providers: only globally-owned (owner 0) envelopes are
        // recovery candidates, capped at RecoveryBatchSize. Envelopes owned by a live
        // node are in flight and must not be re-handed to a sending agent.
        var b = Builders<OutgoingMessage>.Filter;
        var docs = await Outgoing
            .Find(b.And(
                b.Eq(x => x.OwnerId, MongoConstants.AnyNode),
                b.Eq(x => x.Destination, destination.ToString())))
            .Limit(_options.Durability.RecoveryBatchSize)
            .ToListAsync();
        return docs.Select(x => x.Read()).ToList();
    }

    /// <summary>
    /// Upsert keyed on the envelope id: re-storing an envelope replaces its document (owner included).
    /// <see cref="Envelope.WasPersistedInOutbox"/> is set only after the write has been acknowledged
    /// (GH-4371) — a store that has written the row and reports otherwise is wrong, and a failed write
    /// must leave the flag untouched.
    /// </summary>
    public async Task StoreOutgoingAsync(Envelope envelope, int ownerId)
    {
        var doc = new OutgoingMessage(envelope) { OwnerId = ownerId };
        await Outgoing.ReplaceOneAsync(
            Builders<OutgoingMessage>.Filter.Eq(x => x.Id, doc.Id),
            doc, new ReplaceOptions { IsUpsert = true });

        envelope.WasPersistedInOutbox = true;
    }

    /// <summary>
    /// The batch overload (GH-4319): one unordered bulk write of per-envelope upserts, so a batch of
    /// N envelopes costs one <c>update</c> command instead of N round trips, with the same
    /// replacement-by-id semantics as the single write.
    /// <para>
    /// All-or-nothing, matching the RDBMS reference (<c>MessageDatabase.Outgoing.cs</c> wraps the
    /// chunked inserts in one transaction and rolls back on any failure) and Cosmos's
    /// <c>TransactionalBatch</c>: the bulk write runs inside a replica-set transaction, so a failing
    /// envelope aborts the whole batch and nothing is left half-persisted. Only after that transaction
    /// commits are the envelopes flagged <see cref="Envelope.WasPersistedInOutbox"/>; on failure none
    /// is, and the exception surfaces to the caller.
    /// </para>
    /// </summary>
    public async Task StoreOutgoingAsync(IReadOnlyList<Envelope> envelopes, int ownerId)
    {
        if (envelopes.Count == 0) return;

        var models = envelopes.Select(envelope =>
        {
            var doc = new OutgoingMessage(envelope) { OwnerId = ownerId };
            return new ReplaceOneModel<OutgoingMessage>(
                Builders<OutgoingMessage>.Filter.Eq(x => x.Id, doc.Id), doc) { IsUpsert = true };
        }).ToList();

        await InTransactionAsync((s, ct) =>
            Outgoing.BulkWriteAsync(s, models, new BulkWriteOptions { IsOrdered = false }, ct));

        foreach (var envelope in envelopes)
        {
            envelope.WasPersistedInOutbox = true;
        }
    }

    public Task DeleteOutgoingAsync(Envelope envelope)
        => Outgoing.DeleteOneAsync(Builders<OutgoingMessage>.Filter.Eq(x => x.Id, envelope.Id));

    public Task DeleteOutgoingAsync(Envelope[] envelopes)
        => Outgoing.DeleteManyAsync(Builders<OutgoingMessage>.Filter.In(x => x.Id, envelopes.Select(e => e.Id)));

    public async Task DiscardAndReassignOutgoingAsync(Envelope[] discards, Envelope[] reassigned, int nodeId)
    {
        if (discards.Length > 0)
        {
            await DeleteOutgoingAsync(discards);
        }

        if (reassigned.Length > 0)
        {
            await Outgoing.UpdateManyAsync(
                Builders<OutgoingMessage>.Filter.In(x => x.Id, reassigned.Select(e => e.Id)),
                Builders<OutgoingMessage>.Update.Set(x => x.OwnerId, nodeId));
        }
    }
}
