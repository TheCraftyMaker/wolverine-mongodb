using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// One logical-deduplication claim (GH-4180): <c>_id</c> is the deduplication id itself, so the
/// collection's mandatory unique <c>_id</c> index is the database-enforced uniqueness the contract
/// requires; <c>expires</c> is the stored end of the claim's window.
/// </summary>
public class DeduplicationClaimDocument
{
    [BsonId] public string Id { get; set; } = string.Empty;
    [BsonElement("expires")] [BsonRepresentation(BsonType.DateTime)] public DateTimeOffset Expires { get; set; }
}

/// <summary>
/// <see cref="IDeduplicationStore"/> over the <c>wolverine_deduplication</c> collection. Built only when
/// <c>DurabilitySettings.EnableMessageDeduplication</c> is on; otherwise the store returns
/// <see cref="NullDeduplicationStore.Instance"/> and provisions nothing (the contract's requirement).
/// <para>
/// <b>Claim = one insert.</b> <see cref="TryClaimAsync"/> is an <c>InsertOneAsync</c> on the claim's own
/// id: two nodes racing for the same id both hit the same <c>_id</c>, exactly one insert succeeds and the
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
public sealed class MongoDbDeduplicationStore : IDeduplicationStore
{
    private readonly IMongoCollection<DeduplicationClaimDocument> _claims;

    internal MongoDbDeduplicationStore(IMongoDatabase database)
    {
        _claims = database.GetCollection<DeduplicationClaimDocument>(MongoConstants.DeduplicationCollection);
    }

    public bool Enabled => true;

    public async Task<bool> TryClaimAsync(string deduplicationId, DateTimeOffset expires,
        CancellationToken cancellation = default)
    {
        var claim = new DeduplicationClaimDocument { Id = deduplicationId, Expires = expires.ToUniversalTime() };
        try
        {
            await _claims.InsertOneAsync(claim, cancellationToken: cancellation);
            return true;
        }
        catch (MongoWriteException e) when (e.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Already claimed. If that claim's stored window has passed it protects nothing any more, so
            // take it over — atomically, predicated on the expiry, so two late-comers cannot both win.
            var takenOver = await _claims.FindOneAndUpdateAsync(
                Builders<DeduplicationClaimDocument>.Filter.And(
                    Builders<DeduplicationClaimDocument>.Filter.Eq(x => x.Id, deduplicationId),
                    Builders<DeduplicationClaimDocument>.Filter.Lte(x => x.Expires, DateTimeOffset.UtcNow)),
                Builders<DeduplicationClaimDocument>.Update.Set(x => x.Expires, claim.Expires),
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
