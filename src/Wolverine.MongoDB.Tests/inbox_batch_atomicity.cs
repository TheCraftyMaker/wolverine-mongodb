using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// A batch <c>StoreIncomingAsync</c> containing at least one duplicate must persist NOTHING.
/// <para>
/// This is <c>DurableReceiver</c>'s duplicate-retry contract (<c>DurableReceiver.cs:706-717</c>):
/// after a <see cref="DuplicateIncomingEnvelopeException"/> it re-posts every envelope of the failed
/// batch through the per-envelope path — and that path *completes* a duplicate at the listener
/// **without enqueuing it** (<c>:522</c>, <c>:530</c>). So any fresh envelope a failed batch left
/// behind is stranded: persisted, owned by this live node, never handled, and invisible to orphan
/// recovery (which matches only <c>OwnerId == AnyNode</c>,
/// <c>MongoDbMessageStore.Durability.cs:126</c>).
/// </para>
/// </summary>
[Collection("mongodb")]
public class inbox_batch_atomicity
{
    private readonly AppFixture _fixture;
    public inbox_batch_atomicity(AppFixture fixture) => _fixture = fixture;

    private static Envelope freshEnvelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("rabbitmq://queue/batch-atomicity");
        return envelope;
    }

    private static Task<long> countByEnvelopeIdAsync(MongoDbMessageStore store, IEnumerable<Guid> ids)
        => store.Incoming.CountDocumentsAsync(
            Builders<IncomingMessage>.Filter.In(x => x.EnvelopeId, ids.ToList()));

    [Fact]
    public async Task batch_with_one_duplicate_persists_nothing()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var existing = freshEnvelope();
        await store.Inbox.StoreIncomingAsync(existing);

        var fresh = Enumerable.Range(0, 4).Select(_ => freshEnvelope()).ToList();
        var freshIds = fresh.Select(x => x.Id).ToList();

        // Duplicate sits in the middle: documents both before and after it in the batch.
        var batch = new List<Envelope> { fresh[0], fresh[1], existing, fresh[2], fresh[3] };

        var ex = await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => store.Inbox.StoreIncomingAsync(batch));

        // The dupe list must name at least the one genuine duplicate. Inside a transaction the
        // server fails fast, so completeness of this list is deliberately NOT the assertion —
        // the receiver's retry-all contract only needs "≥1 duplicate + nothing persisted".
        ex.Duplicates.Count.ShouldBeGreaterThanOrEqualTo(1);
        ex.Duplicates.Select(x => x.Id).ShouldContain(existing.Id);

        // THE load-bearing assertion: not one of the fresh envelopes may survive the failed batch.
        (await countByEnvelopeIdAsync(store, freshIds)).ShouldBe(0,
            "a fresh envelope left behind by a failed batch is stranded — the receiver's retry " +
            "completes it as a duplicate without ever enqueuing it");

        // The pre-existing envelope was committed by an earlier, separate write and must survive.
        var all = await store.Admin.AllIncomingAsync();
        all.Count.ShouldBe(1);
        all.Single().Id.ShouldBe(existing.Id);
    }

    [Fact]
    public async Task all_fresh_batch_persists_all()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var batch = Enumerable.Range(0, 5).Select(_ => freshEnvelope()).ToList();

        await store.Inbox.StoreIncomingAsync(batch);

        (await countByEnvelopeIdAsync(store, batch.Select(x => x.Id))).ShouldBe(5);
        (await store.Admin.AllIncomingAsync()).Count.ShouldBe(5);
    }

    [Fact]
    public async Task batch_with_only_intra_batch_duplicates_persists_nothing()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        // Nothing pre-exists, so the post-abort existence probe finds nothing; the duplicate is
        // entirely internal to the batch and must be recovered by intra-batch detection.
        var repeated = freshEnvelope();
        var other = freshEnvelope();
        var batch = new List<Envelope> { repeated, other, repeated };

        var ex = await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => store.Inbox.StoreIncomingAsync(batch));

        ex.Duplicates.Select(x => x.Id).ShouldContain(repeated.Id);
        (await store.Admin.AllIncomingAsync()).Count.ShouldBe(0);
    }

    [Fact]
    public async Task single_envelope_store_unaffected()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = freshEnvelope();
        await store.Inbox.StoreIncomingAsync(envelope);
        (await countByEnvelopeIdAsync(store, [envelope.Id])).ShouldBe(1);

        var ex = await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => store.Inbox.StoreIncomingAsync(envelope));
        ex.Duplicates.Single().Id.ShouldBe(envelope.Id);

        // The already-stored document is untouched by the rejected re-store.
        (await store.Admin.AllIncomingAsync()).Count.ShouldBe(1);
    }
}
