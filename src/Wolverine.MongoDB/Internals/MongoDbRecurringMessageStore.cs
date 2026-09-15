using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// One recurring schedule's tracking document (<c>wolverine_recurring_messages</c>, <c>_id</c> = the
/// schedule name). Mirrors <see cref="RecurringMessageRecord"/> field for field; every instant is a
/// UTC BSON Date.
/// </summary>
public class RecurringMessageDocument
{
    [BsonId] public string Id { get; set; } = string.Empty;
    [BsonElement("cronExpression")] public string CronExpression { get; set; } = string.Empty;
    [BsonElement("envelopeIds")] [BsonGuidRepresentation(GuidRepresentation.Standard)] public Guid[] EnvelopeIds { get; set; } = [];
    [BsonElement("deduplicationId")] [BsonIgnoreIfNull] public string? DeduplicationId { get; set; }
    [BsonElement("nextOccurrence")] [BsonIgnoreIfNull] [BsonRepresentation(BsonType.DateTime)] public DateTimeOffset? NextOccurrence { get; set; }
    [BsonElement("paused")] public bool Paused { get; set; }
    [BsonElement("pausedAt")] [BsonIgnoreIfNull] [BsonRepresentation(BsonType.DateTime)] public DateTimeOffset? PausedAt { get; set; }
    [BsonElement("triggerRequestedAt")] [BsonIgnoreIfNull] [BsonRepresentation(BsonType.DateTime)] public DateTimeOffset? TriggerRequestedAt { get; set; }
    [BsonElement("lastUpdated")] [BsonRepresentation(BsonType.DateTime)] public DateTimeOffset LastUpdated { get; set; }

    public RecurringMessageRecord ToRecord() => new()
    {
        Name = Id,
        CronExpression = CronExpression,
        EnvelopeIds = EnvelopeIds,
        DeduplicationId = DeduplicationId,
        NextOccurrence = NextOccurrence,
        Paused = Paused,
        PausedAt = PausedAt,
        TriggerRequestedAt = TriggerRequestedAt,
        LastUpdated = LastUpdated
    };
}

/// <summary>
/// <see cref="IRecurringMessageStore"/> over <c>wolverine_recurring_messages</c>. Built only when
/// <c>DurabilitySettings.EnableRecurringMessages</c> is on (flipped by registering the first schedule
/// through <c>opts.Schedules</c>) and this store is the <c>Main</c> store; otherwise
/// <see cref="NullRecurringMessageStore.Instance"/>, and nothing is provisioned.
/// <para>
/// Semantics follow the upstream RDBMS reference (<c>RdbmsRecurringMessageStore</c>) member for member:
/// a publish upserts the bookkeeping but never un-pauses; pause marks the document and eagerly deletes
/// the tracked <c>Scheduled</c> inbox documents in one transaction (the caller is usually not the node
/// running the agent, and nothing may fire in the gap); resume only clears the mark; a trigger request
/// is refused while paused — atomically, the refusal being the update predicate itself.
/// </para>
/// </summary>
public sealed class MongoDbRecurringMessageStore : IRecurringMessageStore
{
    private readonly MongoDbMessageStore _parent;
    private readonly IMongoCollection<RecurringMessageDocument> _schedules;

    internal MongoDbRecurringMessageStore(MongoDbMessageStore parent, IMongoDatabase database)
    {
        _parent = parent;
        _schedules = database.GetCollection<RecurringMessageDocument>(MongoConstants.RecurringMessagesCollection);
    }

    public bool Enabled => true;

    private static FilterDefinition<RecurringMessageDocument> byName(string name)
        => Builders<RecurringMessageDocument>.Filter.Eq(x => x.Id, name);

    public Task RecordPublishedAsync(RecurringMessageRecord record, CancellationToken token = default)
    {
        // Overwrites the publish bookkeeping only. `paused` / `pausedAt` / `triggerRequestedAt` are left
        // alone on an existing document — a publish is never permission to un-pause — and a brand-new
        // document starts un-paused.
        var update = Builders<RecurringMessageDocument>.Update
            .Set(x => x.CronExpression, record.CronExpression)
            .Set(x => x.EnvelopeIds, record.EnvelopeIds)
            .Set(x => x.DeduplicationId, record.DeduplicationId)
            .Set(x => x.NextOccurrence, record.NextOccurrence)
            .Set(x => x.LastUpdated, record.LastUpdated)
            .SetOnInsert(x => x.Paused, false);

        return _schedules.UpdateOneAsync(byName(record.Name), update, new UpdateOptions { IsUpsert = true }, token);
    }

    public async Task<RecurringMessageRecord?> LoadAsync(string name, CancellationToken token = default)
    {
        var doc = await _schedules.Find(byName(name)).FirstOrDefaultAsync(token);
        return doc?.ToRecord();
    }

    public async Task<IReadOnlyList<RecurringMessageRecord>> LoadAllAsync(CancellationToken token = default)
    {
        var docs = await _schedules.Find(FilterDefinition<RecurringMessageDocument>.Empty)
            .Sort(Builders<RecurringMessageDocument>.Sort.Ascending(x => x.Id))
            .ToListAsync(token);
        return docs.Select(x => x.ToRecord()).ToList();
    }

    public async Task<int> CountStillScheduledAsync(Guid[] envelopeIds, CancellationToken token = default)
    {
        if (envelopeIds.Length == 0) return 0;

        var b = Builders<IncomingMessage>.Filter;
        var count = await _parent.Incoming.CountDocumentsAsync(
            b.And(b.In(x => x.EnvelopeId, envelopeIds), b.Eq(x => x.Status, EnvelopeStatus.Scheduled)),
            cancellationToken: token);
        return (int)count;
    }

    public Task PauseAsync(string name, DateTimeOffset pausedAt, CancellationToken token = default)
        => _parent.InTransactionAsync(async (session, ct) =>
        {
            var existing = await _schedules.Find(session, byName(name)).FirstOrDefaultAsync(ct);
            var trackedIds = existing?.EnvelopeIds ?? [];

            // Idempotent: an already-paused schedule keeps its original PausedAt. A schedule with no
            // document yet gets a paused-only one, so pausing before the first publish works.
            var update = Builders<RecurringMessageDocument>.Update
                .Set(x => x.Paused, true)
                .Set(x => x.PausedAt, existing is { Paused: true, PausedAt: not null } ? existing.PausedAt : pausedAt.ToUniversalTime())
                .Set(x => x.EnvelopeIds, Array.Empty<Guid>())
                .Set(x => x.NextOccurrence, null)
                .Set(x => x.LastUpdated, DateTimeOffset.UtcNow)
                .SetOnInsert(x => x.CronExpression, string.Empty);
            await _schedules.UpdateOneAsync(session, byName(name), update, new UpdateOptions { IsUpsert = true }, ct);

            if (trackedIds.Length > 0)
            {
                // Eagerly cancel the pre-scheduled occurrence(s): delete the tracked inbox documents that
                // are still Scheduled, in the same transaction as the pause mark.
                var b = Builders<IncomingMessage>.Filter;
                await _parent.Incoming.DeleteManyAsync(session,
                    b.And(b.In(x => x.EnvelopeId, trackedIds), b.Eq(x => x.Status, EnvelopeStatus.Scheduled)), cancellationToken: ct);
            }
        }, token);

    public Task ResumeAsync(string name, CancellationToken token = default)
        => _schedules.UpdateOneAsync(byName(name),
            Builders<RecurringMessageDocument>.Update
                .Set(x => x.Paused, false)
                .Set(x => x.PausedAt, null)
                .Set(x => x.LastUpdated, DateTimeOffset.UtcNow),
            cancellationToken: token);

    public async Task<bool> RequestTriggerAsync(string name, DateTimeOffset requestedAt, CancellationToken token = default)
    {
        // The refusal IS the predicate: only a non-paused document matches. With IsUpsert the same statement
        // creates the document for a schedule that has never published — and for a PAUSED schedule the
        // filter matches nothing, so the upsert tries to insert a second document with the same _id and
        // the duplicate key is the atomic "refused" signal.
        var b = Builders<RecurringMessageDocument>.Filter;
        try
        {
            await _schedules.UpdateOneAsync(
                b.And(byName(name), b.Eq(x => x.Paused, false)),
                Builders<RecurringMessageDocument>.Update
                    .Set(x => x.TriggerRequestedAt, requestedAt.ToUniversalTime())
                    .Set(x => x.LastUpdated, DateTimeOffset.UtcNow)
                    .SetOnInsert(x => x.CronExpression, string.Empty)
                    .SetOnInsert(x => x.EnvelopeIds, Array.Empty<Guid>()),
                new UpdateOptions { IsUpsert = true }, token);
            return true;
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return false;
        }
    }

    public Task ClearTriggerAsync(string name, CancellationToken token = default)
        => _schedules.UpdateOneAsync(byName(name),
            Builders<RecurringMessageDocument>.Update
                .Set(x => x.TriggerRequestedAt, null)
                .Set(x => x.LastUpdated, DateTimeOffset.UtcNow),
            cancellationToken: token);
}
