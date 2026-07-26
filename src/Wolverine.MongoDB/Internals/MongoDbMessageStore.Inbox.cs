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

    /// <summary>
    /// MongoDB ignores collection/database-level write concern for operations inside a
    /// transaction — the commit is governed by the transaction's own write concern. The store's
    /// durability pin (<see cref="MongoDbMessageStore"/> ctor: w:majority + j:true) lives on the
    /// database handle and does NOT survive into a transaction, so wrapping a write without
    /// explicit options would silently downgrade it to the consumer's client default (often w:1).
    /// These options restate the pin.
    /// </summary>
    private static readonly TransactionOptions InboxTransactionOptions = new(
        readConcern: ReadConcern.Majority,
        writeConcern: WriteConcern.WMajority.With(journal: true));

    private const int DuplicateKeyErrorCode = 11000;

    /// <summary>
    /// A batch store is all-or-nothing: either every envelope persists or none does.
    /// <para>
    /// <c>DurableReceiver</c> re-posts the whole batch through its per-envelope path after a
    /// <see cref="DuplicateIncomingEnvelopeException"/> (<c>DurableReceiver.cs:706-717</c>), and
    /// that path <em>completes</em> a duplicate at the listener without enqueuing it (<c>:522</c>,
    /// <c>:530</c>). A partially-persisted batch therefore strands its fresh envelopes: stored,
    /// owned by this live node, never handled, and invisible to orphan recovery (which matches only
    /// <c>OwnerId == AnyNode</c>). Transaction-wrapping restores the RDBMS provider's contract
    /// (<c>MessageDatabase.Incoming.cs:174-213</c>).
    /// </para>
    /// </summary>
    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        if (envelopes.Count == 0) return;
        var docs = envelopes.Select(e => new IncomingMessage(e, InboxIdentity(e))).ToList();

        // WithTransactionAsync transparently retries TransientTransactionError /
        // UnknownTransactionCommitResult and aborts automatically if the body throws.
        using var session = await _client.StartSessionAsync();
        try
        {
            await session.WithTransactionAsync(async (s, ct) =>
            {
                // IsOrdered = false is inert with respect to error handling here — inside a
                // transaction the server aborts on the FIRST write error regardless. It is kept
                // for the success path's batching behavior.
                await Incoming.InsertManyAsync(s, docs, new InsertManyOptions { IsOrdered = false }, ct);
                return true;
            }, InboxTransactionOptions);
        }
        catch (Exception e) when (isDuplicateKeyFailure(e))
        {
            // The transaction is already aborted, so nothing from this batch survives and a single
            // existence probe now yields the complete, precise duplicate list — no dependency on
            // which exception shape the driver surfaced, or on how much the fail-fast server
            // managed to report.
            var dupes = await probeForExistingAsync(envelopes);

            if (dupes.Count == 0)
            {
                // Nothing pre-existed, yet a duplicate key was rejected: two envelopes within this
                // batch share an identity. Report the members of every repeated identity group.
                dupes = envelopes.GroupBy(InboxIdentity)
                    .Where(g => g.Count() > 1)
                    .SelectMany(g => g)
                    .Distinct()
                    .ToList();
            }

            // DuplicateIncomingEnvelopeException must never be constructed empty, and a genuinely
            // unexplained failure has to surface as itself.
            if (dupes.Count == 0) throw;
            throw new DuplicateIncomingEnvelopeException(dupes);
        }
    }

    /// <summary>
    /// One query, complete list: every batch identity that exists in the inbox. Only ever called
    /// after the batch transaction aborted, so anything found is a pre-existing document — a
    /// genuine duplicate.
    /// </summary>
    private async Task<List<Envelope>> probeForExistingAsync(IReadOnlyList<Envelope> envelopes)
    {
        var ids = envelopes.Select(InboxIdentity).Distinct().ToList();
        var present = await Incoming
            .Find(Builders<IncomingMessage>.Filter.In(x => x.Id, ids))
            .Project(x => x.Id)
            .ToListAsync();

        var presentSet = present.ToHashSet();
        return envelopes.Where(e => presentSet.Contains(InboxIdentity(e))).ToList();
    }

    /// <summary>
    /// Recognises a duplicate-key failure across every shape the driver can surface for a failed
    /// insert inside a session, by category or by code 11000, walking inner exceptions.
    /// <para>
    /// Verified against MongoDB.Driver 3.10.0 / mongo:7: an in-transaction duplicate surfaces as
    /// <c>MongoBulkWriteException&lt;IncomingMessage&gt;</c> with a <em>single</em> write error
    /// (<c>code=11000</c>, <c>category=DuplicateKey</c>) at the index of the first offending
    /// document — the server fails fast, so later indexes are never attempted and the write-error
    /// list cannot be used to enumerate duplicates. That is why the dupe list comes from a
    /// post-abort probe instead. The remaining branches keep the classifier tolerant of other
    /// shapes rather than dependent on this one.
    /// </para>
    /// <para>
    /// Returns <c>false</c> when the exception carries <em>any</em> non-duplicate write error: a
    /// mixed failure must surface as itself so <c>DurableReceiver.cs:718</c> pauses the listener
    /// and runs the inbox-unavailable path instead of being reported as a duplicate.
    /// </para>
    /// </summary>
    private static bool isDuplicateKeyFailure(Exception exception)
    {
        switch (exception)
        {
            case MongoBulkWriteException bulk:
                if (bulk.WriteErrors.Any(w => !isDuplicateKey(w.Category, w.Code))) return false;
                return bulk.WriteErrors.Any(w => isDuplicateKey(w.Category, w.Code));

            case MongoWriteException write:
                return write.WriteError is { } error && isDuplicateKey(error.Category, error.Code);

            case MongoCommandException command:
                return command.Code == DuplicateKeyErrorCode;

            case AggregateException aggregate:
                return aggregate.InnerExceptions.Any(isDuplicateKeyFailure);

            default:
                return exception.InnerException is { } inner && isDuplicateKeyFailure(inner);
        }
    }

    private static bool isDuplicateKey(ServerErrorCategory category, int code)
        => category == ServerErrorCategory.DuplicateKey || code == DuplicateKeyErrorCode;

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
