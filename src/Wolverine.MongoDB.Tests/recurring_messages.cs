using JasperFx.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// MongoDB-specific facts for <see cref="MongoDbRecurringMessageStore"/>, beside the inherited upstream
/// suite: the opt-in is schema neutral at the store level (no host; the host-level counterpart is the
/// replaced compliance fact in <c>recurring_message_compliance</c>),
/// the store is Main-only, a publish never un-pauses, pause/resume/trigger work from a SECOND store
/// instance (a different node than the scheduler), pause eagerly cancels exactly the tracked Scheduled
/// inbox documents, and the paused-trigger refusal is atomic.
/// </summary>
[Collection("mongodb")]
public class recurring_messages
{
    private readonly AppFixture _fixture;
    public recurring_messages(AppFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    private MongoDbMessageStore buildStore(bool enabled = true, MessageStoreRole role = MessageStoreRole.Main)
    {
        var options = new WolverineOptions();
        options.Durability.EnableRecurringMessages = enabled;
        return new MongoDbMessageStore(_fixture.Client, AppFixture.DatabaseName, options) { Role = role };
    }

    private static RecurringMessageRecord published(string name, params Guid[] envelopeIds) => new()
    {
        Name = name,
        CronExpression = "0 * * * *",
        EnvelopeIds = envelopeIds,
        DeduplicationId = $"{name}:{DateTimeOffset.UtcNow:O}",
        NextOccurrence = DateTimeOffset.UtcNow.AddHours(1),
        LastUpdated = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task the_opt_in_is_schema_neutral()
    {
        await _fixture.ClearAll();
        await Database.DropCollectionAsync(MongoConstants.RecurringMessagesCollection, Ct);

        var without = buildStore(enabled: false);
        await without.Admin.MigrateAsync();
        without.RecurringMessages.ShouldBeSameAs(NullRecurringMessageStore.Instance);
        without.RecurringMessages.Enabled.ShouldBeFalse();
        (await (await Database.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct))
            .ShouldNotContain(MongoConstants.RecurringMessagesCollection);

        var with = buildStore();
        with.RecurringMessages.ShouldBeOfType<MongoDbRecurringMessageStore>();
        with.RecurringMessages.Enabled.ShouldBeTrue();
    }

    [Fact]
    public async Task an_ancillary_store_gets_the_null_store()
    {
        await _fixture.ClearAll();
        buildStore(role: MessageStoreRole.Ancillary).RecurringMessages.ShouldBeSameAs(NullRecurringMessageStore.Instance);
    }

    [Fact]
    public async Task recording_a_publish_never_unpauses_and_a_new_schedule_starts_running()
    {
        await _fixture.ClearAll();
        var store = buildStore().RecurringMessages;

        await store.PauseAsync("nightly", DateTimeOffset.UtcNow, Ct);
        await store.RecordPublishedAsync(published("nightly", Guid.NewGuid()), Ct);

        var paused = await store.LoadAsync("nightly", Ct);
        paused.ShouldNotBeNull();
        paused.Paused.ShouldBeTrue("a publish is never permission to un-pause");
        paused.PausedAt.ShouldNotBeNull();
        paused.EnvelopeIds.Length.ShouldBe(1);

        await store.RecordPublishedAsync(published("hourly", Guid.NewGuid()), Ct);
        var fresh = await store.LoadAsync("hourly", Ct);
        fresh.ShouldNotBeNull();
        fresh.Paused.ShouldBeFalse();
        fresh.PausedAt.ShouldBeNull();
        fresh.CronExpression.ShouldBe("0 * * * *");

        (await store.LoadAllAsync(Ct)).Select(x => x.Name).ShouldBe(["hourly", "nightly"]);
    }

    [Fact]
    public async Task pause_from_another_node_cancels_exactly_the_tracked_scheduled_envelopes_and_is_idempotent()
    {
        await _fixture.ClearAll();
        var scheduler = buildStore();
        var otherNode = buildStore();

        // Two tracked occurrences plus an unrelated scheduled envelope that must survive.
        var tracked1 = ObjectMother.Envelope();
        var tracked2 = ObjectMother.Envelope();
        var unrelated = ObjectMother.Envelope();
        foreach (var e in new[] { tracked1, tracked2, unrelated })
        {
            e.Status = EnvelopeStatus.Scheduled;
            e.ScheduledTime = DateTimeOffset.UtcNow.AddHours(1);
            e.OwnerId = MongoConstants.AnyNode;
            await scheduler.Inbox.StoreIncomingAsync(e);
        }

        await scheduler.RecurringMessages.RecordPublishedAsync(published("reports", tracked1.Id, tracked2.Id), Ct);
        (await scheduler.RecurringMessages.CountStillScheduledAsync([tracked1.Id, tracked2.Id], Ct)).ShouldBe(2);

        var pausedAt = DateTimeOffset.UtcNow;
        await otherNode.RecurringMessages.PauseAsync("reports", pausedAt, Ct);

        // The scheduler node sees the pause through the durable document, and the occurrences are gone.
        var record = await scheduler.RecurringMessages.LoadAsync("reports", Ct);
        record.ShouldNotBeNull();
        record.Paused.ShouldBeTrue();
        record.PausedAt!.Value.ShouldBe(pausedAt, TimeSpan.FromSeconds(1));
        record.EnvelopeIds.ShouldBeEmpty();
        record.NextOccurrence.ShouldBeNull();
        (await scheduler.RecurringMessages.CountStillScheduledAsync([tracked1.Id, tracked2.Id], Ct)).ShouldBe(0);
        (await scheduler.Admin.FetchCountsAsync()).Scheduled.ShouldBe(1, "the unrelated scheduled envelope must survive");

        // Idempotent: a second pause keeps the original PausedAt.
        await otherNode.RecurringMessages.PauseAsync("reports", DateTimeOffset.UtcNow.AddMinutes(5), Ct);
        (await scheduler.RecurringMessages.LoadAsync("reports", Ct))!.PausedAt!.Value.ShouldBe(pausedAt, TimeSpan.FromSeconds(1));

        // Resume from yet another instance clears the mark and never back-fills.
        await buildStore().RecurringMessages.ResumeAsync("reports", Ct);
        var resumed = await scheduler.RecurringMessages.LoadAsync("reports", Ct);
        resumed!.Paused.ShouldBeFalse();
        resumed.PausedAt.ShouldBeNull();
        resumed.EnvelopeIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task pausing_before_the_first_publish_creates_a_paused_only_document_and_resume_of_unknown_is_a_no_op()
    {
        await _fixture.ClearAll();
        var store = buildStore().RecurringMessages;

        await store.PauseAsync("never-published", DateTimeOffset.UtcNow, Ct);
        var record = await store.LoadAsync("never-published", Ct);
        record.ShouldNotBeNull();
        record.Paused.ShouldBeTrue();
        record.EnvelopeIds.ShouldBeEmpty();

        await store.ResumeAsync("unknown", Ct);
        (await store.LoadAsync("unknown", Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task a_trigger_request_is_recorded_for_a_running_schedule_refused_while_paused_and_cleared_once()
    {
        await _fixture.ClearAll();
        var store = buildStore().RecurringMessages;
        var requestedAt = DateTimeOffset.UtcNow;

        // Before the first publish: the request creates the document.
        (await store.RequestTriggerAsync("adhoc", requestedAt, Ct)).ShouldBeTrue();
        var record = await store.LoadAsync("adhoc", Ct);
        record.ShouldNotBeNull();
        record.TriggerRequestedAt!.Value.ShouldBe(requestedAt, TimeSpan.FromSeconds(1));
        record.Paused.ShouldBeFalse();

        await store.ClearTriggerAsync("adhoc", Ct);
        (await store.LoadAsync("adhoc", Ct))!.TriggerRequestedAt.ShouldBeNull();
        await store.ClearTriggerAsync("adhoc", Ct); // idempotent

        // Paused: refused, and the document is untouched (no second document, no trigger slot).
        await store.PauseAsync("adhoc", DateTimeOffset.UtcNow, Ct);
        (await store.RequestTriggerAsync("adhoc", DateTimeOffset.UtcNow, Ct)).ShouldBeFalse();
        var paused = await store.LoadAsync("adhoc", Ct);
        paused!.TriggerRequestedAt.ShouldBeNull();
        (await Database.GetCollection<BsonDocument>(MongoConstants.RecurringMessagesCollection)
            .CountDocumentsAsync(new BsonDocument("_id", "adhoc"), cancellationToken: Ct)).ShouldBe(1);

        // Running again: accepted.
        await store.ResumeAsync("adhoc", Ct);
        (await store.RequestTriggerAsync("adhoc", DateTimeOffset.UtcNow, Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task records_round_trip_every_field_as_utc()
    {
        await _fixture.ClearAll();
        var store = buildStore().RecurringMessages;
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var next = new DateTimeOffset(2026, 10, 1, 4, 30, 0, TimeSpan.FromHours(5.5));
        var record = new RecurringMessageRecord
        {
            Name = "kolkata",
            CronExpression = "0 * * * *",
            EnvelopeIds = ids,
            DeduplicationId = "kolkata:2026-09-30T23:00:00.0000000+00:00",
            NextOccurrence = next,
            LastUpdated = DateTimeOffset.UtcNow
        };

        await store.RecordPublishedAsync(record, Ct);
        var loaded = await store.LoadAsync("kolkata", Ct);

        loaded.ShouldNotBeNull();
        loaded.EnvelopeIds.ShouldBe(ids);
        loaded.DeduplicationId.ShouldBe(record.DeduplicationId);
        loaded.NextOccurrence.ShouldBe(next, "the instant is preserved (offsets normalise to UTC)");
        loaded.NextOccurrence!.Value.Offset.ShouldBe(TimeSpan.Zero);
        loaded.LastUpdated.ShouldBe(record.LastUpdated, TimeSpan.FromMilliseconds(5));
    }
}
