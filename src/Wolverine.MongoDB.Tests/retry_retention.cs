using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// A retry that moves an inbox document back to <see cref="EnvelopeStatus.Scheduled"/> must not
/// leave it eligible for the handled-marker TTL. Unlike the RDBMS providers, whose expiry sweep is
/// status-gated (<c>where status = 'Handled' and keep_until &lt;= now</c>), MongoDB's TTL index on
/// <c>keepUntil</c> removes ANY document whose <c>keepUntil</c> has passed — so a rescheduled retry
/// that kept its handled-marker <c>keepUntil</c> would be deleted by the TTL monitor before it ran.
/// The proof of "not eligible" is structural: the TTL index only considers documents that carry
/// the indexed field, so the element must be absent after the reschedule.
/// </summary>
[Collection("mongodb")]
public class retry_retention
{
    private readonly AppFixture _fixture;
    public retry_retention(AppFixture fixture) => _fixture = fixture;

    private IMongoCollection<BsonDocument> rawIncoming()
        => _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<BsonDocument>(MongoConstants.IncomingCollection);

    private static FilterDefinition<BsonDocument> byEnvelopeId(Guid id)
        => new BsonDocument("envelopeId", new BsonBinaryData(id, GuidRepresentation.Standard));

    private static Envelope incomingEnvelope(string destination)
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri(destination);
        envelope.OwnerId = 5;
        return envelope;
    }

    [Fact]
    public async Task rescheduling_a_handled_envelope_drops_keep_until_and_keeps_the_body()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var envelope = incomingEnvelope("local://retry-retention/handled");
        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var handled = await rawIncoming().Find(byEnvelopeId(envelope.Id)).SingleAsync();
        handled.Contains("keepUntil").ShouldBeTrue("precondition: the handled marker carries keepUntil");

        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        envelope.ScheduledTime = scheduledTime;
        envelope.Attempts = 2;
        await store.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        var doc = await rawIncoming().Find(byEnvelopeId(envelope.Id)).SingleAsync();
        doc.Contains("keepUntil").ShouldBeFalse(
            "a Scheduled retry must not carry keepUntil, or the TTL index deletes it before it runs");
        doc["status"].AsString.ShouldBe(nameof(EnvelopeStatus.Scheduled));
        doc["ownerId"].AsInt32.ShouldBe(MongoConstants.AnyNode);
        doc["attempts"].AsInt32.ShouldBe(2);

        var reloaded = (await store.Admin.AllIncomingAsync()).Single();
        reloaded.Id.ShouldBe(envelope.Id);
        reloaded.Status.ShouldBe(EnvelopeStatus.Scheduled);
        reloaded.OwnerId.ShouldBe(MongoConstants.AnyNode);
        reloaded.KeepUntil.ShouldBeNull();
        reloaded.ScheduledTime!.Value.ShouldBe(scheduledTime, TimeSpan.FromSeconds(1));
        reloaded.Data.ShouldBe(envelope.Data);
        reloaded.MessageType.ShouldBe(envelope.MessageType);
        reloaded.Destination.ShouldBe(envelope.Destination);
    }

    [Fact]
    public async Task rescheduling_a_body_less_eager_handled_marker_restores_the_payload()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var original = incomingEnvelope("local://retry-retention/eager");

        // The eager idempotency check persists exactly this shape BEFORE the handler runs
        // (Envelope.ForPersistedHandled): no body, Status = Handled, KeepUntil stamped.
        var marker = Envelope.ForPersistedHandled(original, DateTimeOffset.UtcNow, new WolverineOptions().Durability);
        await store.Inbox.StoreIncomingAsync(marker);

        var stored = await store.Incoming
            .Find(Builders<IncomingMessage>.Filter.Eq(x => x.EnvelopeId, original.Id)).SingleAsync();
        stored.Body.ShouldBeEmpty("precondition: the eager marker is body-less");
        stored.KeepUntil.ShouldNotBeNull();

        // The handler then fails and the error policy schedules a retry of the LIVE envelope,
        // which still carries the payload.
        original.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(1);
        original.Attempts = 1;
        await store.Inbox.RescheduleExistingEnvelopeForRetryAsync(original);

        var docs = await rawIncoming().Find(byEnvelopeId(original.Id)).ToListAsync();
        docs.Count.ShouldBe(1, "the marker is reused, not duplicated");
        docs[0].Contains("keepUntil").ShouldBeFalse();
        docs[0]["body"].AsByteArray.Length.ShouldBeGreaterThan(0, "the retry must carry the message payload");

        var reloaded = (await store.Admin.AllIncomingAsync()).Single();
        reloaded.Id.ShouldBe(original.Id);
        reloaded.Status.ShouldBe(EnvelopeStatus.Scheduled);
        reloaded.OwnerId.ShouldBe(MongoConstants.AnyNode);
        reloaded.Data.ShouldBe(original.Data);
        reloaded.MessageType.ShouldBe(original.MessageType);
        reloaded.Destination.ShouldBe(original.Destination);
        reloaded.ScheduledTime!.Value.ShouldBe(original.ScheduledTime.Value, TimeSpan.FromSeconds(1));
        reloaded.Attempts.ShouldBe(1);
        reloaded.KeepUntil.ShouldBeNull();
    }

    [Fact]
    public async Task consecutive_reschedules_converge_on_one_document_and_a_missing_document_is_inserted()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        // ProcessInline retry #1: nothing is stored yet, so the reschedule inserts.
        var envelope = incomingEnvelope("local://retry-retention/inline");
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(1);
        envelope.Attempts = 1;
        await store.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        // Retry #2 finds the previous Scheduled document and updates it in place.
        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);
        envelope.Attempts = 2;
        await store.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        var all = (await store.Admin.AllIncomingAsync()).Where(x => x.Id == envelope.Id).ToList();
        all.Count.ShouldBe(1);
        all[0].Attempts.ShouldBe(2);
        all[0].OwnerId.ShouldBe(MongoConstants.AnyNode);
        all[0].Status.ShouldBe(EnvelopeStatus.Scheduled);
        all[0].ScheduledTime!.Value.ShouldBe(envelope.ScheduledTime.Value, TimeSpan.FromSeconds(1));
        all[0].KeepUntil.ShouldBeNull();
        all[0].Data.ShouldBe(envelope.Data);
    }

    [Fact]
    public async Task schedule_execution_on_a_handled_document_also_drops_keep_until()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var envelope = incomingEnvelope("local://retry-retention/schedule-execution");
        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(3);
        await store.Inbox.ScheduleExecutionAsync(envelope);

        var doc = await rawIncoming().Find(byEnvelopeId(envelope.Id)).SingleAsync();
        doc.Contains("keepUntil").ShouldBeFalse();
        doc["status"].AsString.ShouldBe(nameof(EnvelopeStatus.Scheduled));
        doc["ownerId"].AsInt32.ShouldBe(MongoConstants.AnyNode);
    }

    [Fact]
    public async Task the_identity_key_is_preserved_across_a_reschedule()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var envelope = incomingEnvelope("local://retry-retention/identity");
        await store.Inbox.StoreIncomingAsync(envelope);
        var before = (await store.Incoming.Find(Builders<IncomingMessage>.Filter.Eq(x => x.EnvelopeId, envelope.Id)).SingleAsync()).Id;

        envelope.ScheduledTime = DateTimeOffset.UtcNow.AddMinutes(1);
        await store.Inbox.RescheduleExistingEnvelopeForRetryAsync(envelope);

        var after = await store.Incoming.Find(Builders<IncomingMessage>.Filter.Eq(x => x.EnvelopeId, envelope.Id)).SingleAsync();
        after.Id.ShouldBe(before, "the document _id is the inbox identity and must not change");
        after.EnvelopeId.ShouldBe(envelope.Id);
        after.ReceivedAt.ShouldBe(envelope.Destination!.ToString());
    }
}
