using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// One logical-deduplication claim (GH-4180): <c>_id</c> is the deduplication id itself, so the
/// collection's mandatory unique <c>_id</c> index is the database-enforced uniqueness the contract
/// requires; <c>expires</c> is the stored end of the claim's window; <c>envelopeId</c> is the envelope that took
/// the claim, absent for a claim made without one.
/// </summary>
public class DeduplicationClaimDocument
{
    [BsonId] public string Id { get; set; } = string.Empty;
    [BsonElement("expires")] [BsonRepresentation(BsonType.DateTime)] public DateTimeOffset Expires { get; set; }

    [BsonElement("envelopeId")]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    [BsonIgnoreIfNull]
    public Guid? EnvelopeId { get; set; }
}

/// <summary>
/// <see cref="IDeduplicationStore"/> over the <c>wolverine_deduplication</c> collection. Built only when
/// <c>DurabilitySettings.MessageDeduplicationMode</c> is not <c>None</c>; otherwise the store returns
/// <see cref="NullDeduplicationStore.Instance"/> and provisions nothing (the contract's requirement).
/// <para>
/// <b>Claim = one insert.</b> <see cref="TryClaimAsync(string, DateTimeOffset, CancellationToken)"/> is an
/// <c>InsertOneAsync</c> on the claim's own id: two nodes racing for the same id both hit the same <c>_id</c>, exactly one insert succeeds and the
/// other gets a duplicate-key error — no read-then-write window. A duplicate whose <em>stored</em>
/// window has already passed is then taken over atomically (<c>findAndModify</c> predicated on
/// <c>expires &lt;= now</c>), so the configured retention window is honoured to the instant rather than
/// to the reaper's cadence. The stored instant, never the current setting, decides: shortening
/// <c>DeduplicationWindow</c> later cannot retroactively un-claim an id.
/// </para>
/// <para>
/// <b>The claim is deliberately sessionless.</b> Wolverine core weaves the claim frame in <em>before</em>
/// the persistence provider's transaction frame opens a session, and no upstream hook hands the
/// claim an <c>IClientSessionHandle</c>; the RDBMS reference implementation claims on its own connection
/// for the same reason. That also means a duplicate-key error here can never abort a handler
/// transaction (the MongoDB behaviour that makes an in-transaction duplicate insert poison the whole
/// transaction), because there is no transaction on this write. What makes the claim safe for a
/// <em>transactional</em> handler is <c>TransactionalFrame</c>: when the handler's transaction rolls
/// back, the frame releases the claim so the retry is not refused as a duplicate of its own failed
/// attempt. Non-transactional handlers get the same compensation from core's own release frame.
/// </para>
/// <para>
/// Expiry: a TTL index on <c>expires</c> lets the server reap stale claims on its own cadence;
/// <see cref="DeleteExpiredAsync"/> is the contract's explicit reaper and reports what it removed.
/// </para>
/// </summary>
public sealed class MongoDbDeduplicationStore : IDeduplicationStore, IEnvelopeAwareDeduplicationStore,
    IDeduplicationClaimLookup
{
    private readonly IMongoCollection<DeduplicationClaimDocument> _claims;

    internal MongoDbDeduplicationStore(IMongoDatabase database)
    {
        _claims = database.GetCollection<DeduplicationClaimDocument>(MongoConstants.DeduplicationCollection);
    }

    public bool Enabled => true;

    public Task<bool> TryClaimAsync(string deduplicationId, DateTimeOffset expires,
        CancellationToken cancellation = default)
        => claimAsync(deduplicationId, null, expires, cancellation);

    /// <summary>
    /// A repeat from the envelope that already holds the claim takes it over, which is how inbox recovery gets its
    /// own stranded claim back; a different envelope, or a claim recorded with no envelope, is still refused.
    /// </summary>
    public Task<bool> TryClaimAsync(string deduplicationId, Guid envelopeId, DateTimeOffset expires,
        CancellationToken cancellation = default)
        => claimAsync(deduplicationId, envelopeId, expires, cancellation);

    /// <summary>
    /// Whether a claim is held right now. A claim whose stored window has passed is not held: a claim would take it
    /// over, so the two answers agree.
    /// </summary>
    public async Task<bool> IsClaimedAsync(string deduplicationId, CancellationToken cancellation = default)
        => await _claims.CountDocumentsAsync(
            Builders<DeduplicationClaimDocument>.Filter.And(
                Builders<DeduplicationClaimDocument>.Filter.Eq(x => x.Id, deduplicationId),
                Builders<DeduplicationClaimDocument>.Filter.Gt(x => x.Expires, DateTimeOffset.UtcNow)),
            new CountOptions { Limit = 1 }, cancellation) > 0;

    private async Task<bool> claimAsync(string deduplicationId, Guid? envelopeId, DateTimeOffset expires,
        CancellationToken cancellation)
    {
        var claim = new DeduplicationClaimDocument
        {
            Id = deduplicationId, Expires = expires.ToUniversalTime(), EnvelopeId = envelopeId
        };
        try
        {
            await _claims.InsertOneAsync(claim, cancellationToken: cancellation);
            return true;
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Already claimed. The claim is taken over when its stored window has passed, since it protects nothing
            // any more, or when the same envelope holds it. One conditional update decides, so two late-comers
            // cannot both win.
            var filter = Builders<DeduplicationClaimDocument>.Filter;
            var passed = filter.Lte(x => x.Expires, DateTimeOffset.UtcNow);
            var takeable = envelopeId is { } id ? filter.Or(passed, filter.Eq(x => x.EnvelopeId, id)) : passed;

            var update = Builders<DeduplicationClaimDocument>.Update.Set(x => x.Expires, claim.Expires);
            update = envelopeId is { } owner
                ? update.Set(x => x.EnvelopeId, owner)
                : update.Unset(x => x.EnvelopeId);

            var takenOver = await _claims.FindOneAndUpdateAsync(
                filter.And(filter.Eq(x => x.Id, deduplicationId), takeable), update,
                new FindOneAndUpdateOptions<DeduplicationClaimDocument> { ReturnDocument = ReturnDocument.After },
                cancellation);

            return takenOver is not null;
        }
    }

    /// <summary>Idempotent: deleting a claim that is already gone is a no-op.</summary>
    public Task ReleaseAsync(string deduplicationId, CancellationToken cancellation = default)
        => _claims.DeleteOneAsync(Builders<DeduplicationClaimDocument>.Filter.Eq(x => x.Id, deduplicationId), cancellation);

    public async Task<int> DeleteExpiredAsync(DateTimeOffset utcNow, CancellationToken cancellation = default)
    {
        var result = await _claims.DeleteManyAsync(
            Builders<DeduplicationClaimDocument>.Filter.Lte(x => x.Expires, utcNow.ToUniversalTime()), cancellation);
        return (int)result.DeletedCount;
    }

    /// <summary>TTL index on <c>expires</c>: the server reaps stale claims on its own cadence.</summary>
    internal Task EnsureIndexesAsync()
        => _claims.Indexes.CreateOneAsync(new CreateIndexModel<DeduplicationClaimDocument>(
            Builders<DeduplicationClaimDocument>.IndexKeys.Ascending(x => x.Expires),
            new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }));
}
