using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// The scheduled-message poll has two phases and both must assert the SAME predicate.
/// <para>
/// <c>PublishDueScheduledMessagesAsync</c> (<c>MongoDbMessageStore.Durability.cs</c>) captures one
/// <c>now</c>, selects <c>Status == Scheduled &amp;&amp; ExecutionTime &lt;= now</c> in a single
/// round trip, then claims each selected document one <c>FindOneAndUpdate</c> at a time. Between
/// those two phases a management call to <c>IScheduledMessages.RescheduleAsync</c>
/// (<c>MongoDbMessageStore.ScheduledMessages.cs</c>) can commit — and it writes only
/// <c>ExecutionTime</c>, deliberately leaving <c>Status == Scheduled</c>. So a claim guarded on
/// status alone still matches, and a message pushed an hour into the future is flipped to
/// <c>Incoming</c> and enqueued for immediate execution.
/// </para>
/// <para>
/// Every fact here drives <see cref="MongoDbMessageStore.TryClaimDueScheduledMessageAsync" />
/// directly, so "the interleave" is one explicit call ordering — no host, no runtime, no threads,
/// no sleeps. <c>now</c> is the value the select phase would have captured.
/// </para>
/// </summary>
[Collection("mongodb")]
public class scheduled_claim_recheck
{
    private const int ClaimingNode = 7;
    private static readonly Uri Destination = new("rabbitmq://queue/scheduled-claim");

    private readonly AppFixture _fixture;
    public scheduled_claim_recheck(AppFixture fixture) => _fixture = fixture;

    private static async Task<Envelope> storeScheduled(MongoDbMessageStore store, DateTimeOffset executeAt)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = Destination;
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.OwnerId = MongoConstants.AnyNode; // a scheduled envelope is globally owned
        envelope.ScheduledTime = executeAt;
        await store.Inbox.StoreIncomingAsync(envelope);
        return envelope;
    }

    // Reads the persisted document. Its _id is the inbox identity string, so never reconstruct it.
    private static Task<IncomingMessage> docFor(MongoDbMessageStore store, Guid envelopeId)
        => store.Incoming.Find(Builders<IncomingMessage>.Filter.Eq(x => x.EnvelopeId, envelopeId)).SingleAsync();

    /// <summary>
    /// THE regression: a reschedule that lands between the select and this document's claim must
    /// refuse the claim. Without the execution-time conjunct the message executes an hour early
    /// AND the operator's reschedule is silently lost (the document is then <c>Incoming</c>, which
    /// makes every later <c>RescheduleAsync</c> for it a no-op).
    /// </summary>
    [Fact]
    public async Task claim_is_refused_when_the_message_was_rescheduled_into_the_future()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = await storeScheduled(store, DateTimeOffset.UtcNow.AddMinutes(-5));
        var doc = await docFor(store, envelope.Id);

        // The select phase has happened: `now` is captured and this document was due for it.
        var now = DateTimeOffset.UtcNow;

        // THE INTERLEAVE: a management reschedule commits before this document's claim.
        await store.ScheduledMessages.RescheduleAsync(envelope.Id, now.AddHours(1), CancellationToken.None);

        var claimed = await store.TryClaimDueScheduledMessageAsync(
            doc.Id, ClaimingNode, now, CancellationToken.None);

        claimed.ShouldBeNull(
            "a message rescheduled into the future between select and claim must not be claimed — " +
            "claiming it enqueues it for immediate execution an hour early");

        var after = await docFor(store, envelope.Id);
        after.Status.ShouldBe(EnvelopeStatus.Scheduled, "a refused claim must leave the document schedulable");
        after.OwnerId.ShouldBe(MongoConstants.AnyNode);
        after.ExecutionTime!.Value.ShouldBeGreaterThan(now, "the reschedule must survive the refused claim");
    }

    /// <summary>
    /// Positive control: the narrowed filter must still claim a genuinely due message. Guards the
    /// regression above against passing vacuously.
    /// </summary>
    [Fact]
    public async Task claim_succeeds_for_a_message_that_is_still_due()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = await storeScheduled(store, DateTimeOffset.UtcNow.AddMinutes(-5));
        var doc = await docFor(store, envelope.Id);

        var now = DateTimeOffset.UtcNow;

        var claimed = await store.TryClaimDueScheduledMessageAsync(
            doc.Id, ClaimingNode, now, CancellationToken.None);

        claimed.ShouldNotBeNull();
        claimed.Status.ShouldBe(EnvelopeStatus.Incoming);
        claimed.OwnerId.ShouldBe(ClaimingNode);
    }

    /// <summary>
    /// Boundary: the claim must reuse the instant the select captured, not a fresh
    /// <c>DateTimeOffset.UtcNow</c>. <c>ExecutionTime</c> is a millisecond-precision BSON Date, so
    /// the same value serialized through the same member serializer truncates identically on both
    /// sides; a fresh, later, sub-millisecond instant would not.
    /// </summary>
    [Fact]
    public async Task claim_succeeds_when_execution_time_equals_the_captured_now()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var now = DateTimeOffset.UtcNow;
        var envelope = await storeScheduled(store, now); // stored value and filter value are the SAME
        var doc = await docFor(store, envelope.Id);

        var claimed = await store.TryClaimDueScheduledMessageAsync(
            doc.Id, ClaimingNode, now, CancellationToken.None);

        claimed.ShouldNotBeNull("a message due exactly at the captured instant must still be claimed");
    }

    /// <summary>
    /// Semantics pin: the guard is "still due" (<c>ExecutionTime &lt;= now</c>), not "unchanged"
    /// (<c>ExecutionTime == the select-phase snapshot</c>). A reschedule to a different PAST
    /// instant leaves the message due, so the claim must proceed.
    /// </summary>
    [Fact]
    public async Task claim_succeeds_when_rescheduled_to_a_different_past_instant()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var now = DateTimeOffset.UtcNow;
        var envelope = await storeScheduled(store, now.AddMinutes(-10));
        var doc = await docFor(store, envelope.Id);

        await store.ScheduledMessages.RescheduleAsync(envelope.Id, now.AddMinutes(-1), CancellationToken.None);

        var claimed = await store.TryClaimDueScheduledMessageAsync(
            doc.Id, ClaimingNode, now, CancellationToken.None);

        claimed.ShouldNotBeNull("a reschedule that keeps the message due must not block the claim");
        claimed.OwnerId.ShouldBe(ClaimingNode);
    }

    /// <summary>
    /// The sibling interleave, already safe: <c>CancelAsync</c> DELETES the document
    /// (<c>MongoDbMessageStore.ScheduledMessages.cs</c>), so the claim matches nothing. Keeps the
    /// status guard's stated purpose honest — reschedule is the only mutation that leaves a
    /// selected document claimable but no longer due.
    /// </summary>
    [Fact]
    public async Task claim_is_refused_when_the_message_was_cancelled()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = await storeScheduled(store, DateTimeOffset.UtcNow.AddMinutes(-5));
        var doc = await docFor(store, envelope.Id);

        var now = DateTimeOffset.UtcNow;

        await store.ScheduledMessages.CancelAsync(
            new ScheduledMessageQuery { MessageIds = [envelope.Id] }, CancellationToken.None);

        var claimed = await store.TryClaimDueScheduledMessageAsync(
            doc.Id, ClaimingNode, now, CancellationToken.None);

        claimed.ShouldBeNull("a cancelled message is deleted, so nothing is left to claim");
    }

    /// <summary>
    /// A document already claimed by a competing node (or a prior pass) must not be claimed again.
    /// This is the exactly-once property the status guard owns, pinned here so the added
    /// execution-time conjunct is never mistaken for the thing that provides it.
    /// </summary>
    [Fact]
    public async Task claim_is_refused_when_another_node_already_claimed_it()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var envelope = await storeScheduled(store, DateTimeOffset.UtcNow.AddMinutes(-5));
        var doc = await docFor(store, envelope.Id);

        var now = DateTimeOffset.UtcNow;

        const int competitorNode = 999;
        (await store.TryClaimDueScheduledMessageAsync(doc.Id, competitorNode, now, CancellationToken.None))
            .ShouldNotBeNull();

        (await store.TryClaimDueScheduledMessageAsync(doc.Id, ClaimingNode, now, CancellationToken.None))
            .ShouldBeNull("a due message is claimed exactly once across competing nodes");

        (await docFor(store, envelope.Id)).OwnerId.ShouldBe(competitorNode);
    }
}
