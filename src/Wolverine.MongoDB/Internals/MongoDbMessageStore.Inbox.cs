using MongoDB.Driver;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;

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

        try
        {
            // IsOrdered = false is inert with respect to error handling here — inside a
            // transaction the server aborts on the FIRST write error regardless. It is kept
            // for the success path's batching behavior.
            await InTransactionAsync((s, ct) =>
                Incoming.InsertManyAsync(s, docs, new InsertManyOptions { IsOrdered = false }, ct));
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
        => MarkIncomingEnvelopeAsHandledAsync(new[] { envelope });

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

    /// <summary>
    /// Shared by <see cref="ScheduleExecutionAsync"/> and
    /// <see cref="RescheduleExistingEnvelopeForRetryAsync"/> — both move an incoming envelope back
    /// to <see cref="EnvelopeStatus.Scheduled"/>, released to <see cref="MongoConstants.AnyNode"/>,
    /// with the envelope's current execution time and attempt count. One definition so the two call
    /// sites can't drift.
    /// <para>
    /// The document being moved may be a <em>handled</em> one: <c>MarkIncomingEnvelopeAsHandledAsync</c>
    /// stamps <c>keepUntil</c> and the eager idempotency check stores a body-less handled marker
    /// (<see cref="Envelope.ForPersistedHandled"/>) before the handler even runs. Two things must
    /// therefore happen on the way back to Scheduled, or the retry is lost:
    /// </para>
    /// <list type="bullet">
    /// <item><description><c>keepUntil</c> is <b>unset</b>. The TTL index on that element
    /// (<c>MongoDbMessageStore.Admin.cs</c>) is not status-gated the way the RDBMS expiry sweep is
    /// (<c>where status = 'Handled' and keep_until &lt;= now</c>): a Scheduled document that still
    /// carries the handled marker's <c>keepUntil</c> is deleted by the TTL monitor before it is due.
    /// The TTL index ignores documents without the field, so unsetting it — not nulling it — is the
    /// proof of ineligibility.</description></item>
    /// <item><description>The payload is <b>restored</b> from the live envelope when it has one:
    /// <c>body</c>, <c>messageType</c> and <c>receivedAt</c> are rewritten so a body-less marker becomes
    /// a runnable retry instead of an envelope with empty <c>Data</c>. An envelope without a payload
    /// leaves the stored body alone.</description></item>
    /// </list>
    /// The filter is the inbox identity (<c>_id</c>) and <c>envelopeId</c> is never touched, so the
    /// document keeps its identity; ownership is released to <see cref="MongoConstants.AnyNode"/> so
    /// the scheduled poller on any node can claim it.
    /// </summary>
    private static UpdateDefinition<IncomingMessage> SchedulingUpdate(Envelope envelope)
    {
        var update = Builders<IncomingMessage>.Update
            .Set(x => x.ExecutionTime, envelope.ScheduledTime?.ToUniversalTime())
            .Set(x => x.Status, EnvelopeStatus.Scheduled)
            .Set(x => x.Attempts, envelope.Attempts)
            .Set(x => x.OwnerId, MongoConstants.AnyNode)
            .Unset(x => x.KeepUntil);

        if (envelope.Data is { Length: > 0 })
        {
            update = update
                .Set(x => x.Body, EnvelopeSerializer.Serialize(envelope))
                .Set(x => x.MessageType, envelope.MessageType!)
                .Set(x => x.ReceivedAt, envelope.Destination?.ToString());
        }

        return update;
    }

    public Task ScheduleExecutionAsync(Envelope envelope)
    {
        var id = InboxIdentity(envelope);
        return Incoming.UpdateOneAsync(Builders<IncomingMessage>.Filter.Eq(x => x.Id, id), SchedulingUpdate(envelope));
    }

    public async Task RescheduleExistingEnvelopeForRetryAsync(Envelope envelope)
    {
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = MongoConstants.AnyNode;
        var id = InboxIdentity(envelope);
        var result = await Incoming.UpdateOneAsync(Builders<IncomingMessage>.Filter.Eq(x => x.Id, id), SchedulingUpdate(envelope));
        if (result.MatchedCount == 0)
        {
            await StoreIncomingAsync(envelope);
        }
    }

    /// <summary>
    /// Moves a failed envelope out of the inbox and into <c>wolverine_dead_letters</c>.
    /// <para>
    /// The dead-letter key follows the store's message-identity unit
    /// (<see cref="DeadLetterKey"/>), not the bare envelope Guid: in
    /// <see cref="MessageIdentity.IdAndDestination"/> mode two failed deliveries of one Guid to
    /// different destinations are distinct units of work and must survive as two documents, or the
    /// id-keyed upsert below silently replaces one with the other.
    /// </para>
    /// <para>
    /// This method also serves send-side failures (<c>SendingEnvelopeLifecycle</c>,
    /// <c>MessageContext</c>), where the incoming delete is a no-op; the derived key then embeds
    /// the sending destination. That matches the RDBMS providers, which populate the same
    /// <c>received_at</c> primary-key column from <c>envelope.Destination</c> for send-side dead
    /// letters too.
    /// </para>
    /// </summary>
    public async Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception? exception)
    {
        var dlqId = DeadLetterKey(envelope);

        // Guard body serialization: a poison message whose envelope fails to serialize must
        // still leave the inbox. Build the DLQ doc with a safe/empty body in that case rather
        // than letting the move throw and strand the message in incoming forever.
        DeadLetterMessage dlq;
        try
        {
            dlq = new DeadLetterMessage(envelope, exception, dlqId);
        }
        catch (Exception serializeFailure)
        {
            dlq = DeadLetterMessage.ForUnserializableEnvelope(envelope, exception, serializeFailure, dlqId);
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
        await InTransactionAsync(async (s, ct) =>
        {
            await DeadLetterDocs.ReplaceOneAsync(s,
                Builders<DeadLetterMessage>.Filter.Eq(x => x.Id, dlq.Id),
                dlq, new ReplaceOptions { IsUpsert = true }, ct);

            await Incoming.DeleteOneAsync(s, Builders<IncomingMessage>.Filter.Eq(x => x.Id, id),
                cancellationToken: ct);
        });
    }

    public Task ReleaseIncomingAsync(int ownerId, Uri receivedAt)
        => Incoming.UpdateManyAsync(
            Builders<IncomingMessage>.Filter.And(
                Builders<IncomingMessage>.Filter.Eq(x => x.OwnerId, ownerId),
                Builders<IncomingMessage>.Filter.Eq(x => x.ReceivedAt, receivedAt.ToString())),
            Builders<IncomingMessage>.Update.Set(x => x.OwnerId, MongoConstants.AnyNode));
}
