using JasperFx.Core;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.MongoDB.Tests;

/// <summary>
/// In MessageIdentity.IdAndDestination mode the message identity unit is the
/// (envelope id, destination) pair, so one envelope Guid legitimately has one dead letter
/// per destination — exactly as the RDBMS providers model it by adding <c>received_at</c> to the
/// dead-letter primary key in that mode (Wolverine.Postgresql/Schema/DeadLettersTable.cs:19-26).
/// The document key must therefore follow the identity unit, while the framework-facing Guid
/// (which is all <see cref="Wolverine.Persistence.Durability.IDeadLetters"/> can express) lives in
/// its own <c>envelopeId</c> element, mirroring <see cref="IncomingMessage"/>.
/// </summary>
[Collection("mongodb")]
public class dead_letter_identity
{
    private static readonly Uri DestinationOne = new("rabbitmq://queue/one");
    private static readonly Uri DestinationTwo = new("rabbitmq://queue/two");

    private readonly AppFixture _fixture;
    public dead_letter_identity(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task both_failed_deliveries_survive_in_id_and_destination_mode()
    {
        var store = BuildIdAndDestinationStore();
        await store.Admin.RebuildAsync();
        var (first, _) = await DeadLetterSameIdAtTwoDestinations(store);

        var results = await store.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(), CancellationToken.None);

        results.TotalCount.ShouldBe(2);
        results.Envelopes.Select(x => x.ReceivedAt).OrderBy(x => x)
            .ShouldBe([DestinationOne.ToString(), DestinationTwo.ToString()]);
        results.Envelopes.Select(x => x.Id).Distinct().ShouldBe([first.Id]);
        results.Envelopes.Select(x => x.ExceptionMessage).OrderBy(x => x).ShouldBe(["boom-one", "boom-two"]);
    }

    [Fact]
    public async Task replay_restores_one_incoming_document_per_destination()
    {
        var store = BuildIdAndDestinationStore();
        await store.Admin.RebuildAsync();
        var (first, _) = await DeadLetterSameIdAtTwoDestinations(store);

        // ReplayAsync(MessageIds) must flag every document for the Guid, matching
        // MessageDatabase.DeadLetterAdminService.cs:187-199.
        await store.DeadLetters.ReplayAsync(
            new DeadLetterEnvelopeQuery { MessageIds = [first.Id] }, CancellationToken.None);
        await store.ReplayDeadLettersAsync(CancellationToken.None);

        (await store.Admin.FetchCountsAsync()).DeadLetter.ShouldBe(0);

        var incoming = await store.Admin.AllIncomingAsync();
        incoming.Count.ShouldBe(2);
        incoming.Select(x => x.Destination!.ToString()).OrderBy(x => x)
            .ShouldBe([DestinationOne.ToString(), DestinationTwo.ToString()]);
    }

    [Fact]
    public async Task summaries_report_a_count_per_destination()
    {
        var store = BuildIdAndDestinationStore();
        await store.Admin.RebuildAsync();
        await DeadLetterSameIdAtTwoDestinations(store);

        var summaries = await store.DeadLetters.SummarizeAllAsync("tests", TimeRange.AllTime(), CancellationToken.None);

        summaries.Count.ShouldBe(2);
        summaries.Select(x => x.ReceivedAt.ToString()).OrderBy(x => x)
            .ShouldBe([DestinationOne.ToString(), DestinationTwo.ToString()]);
        summaries.ShouldAllBe(x => x.Count == 1);
    }

    [Fact]
    public async Task discard_by_message_id_removes_every_destination()
    {
        var store = BuildIdAndDestinationStore();
        await store.Admin.RebuildAsync();
        var (first, _) = await DeadLetterSameIdAtTwoDestinations(store);

        (await store.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(), CancellationToken.None))
            .TotalCount.ShouldBe(2);

        // DiscardAsync(MessageIds) deletes all N, matching
        // MessageDatabase.DeadLetterAdminService.cs:176-185.
        await store.DeadLetters.DiscardAsync(
            new DeadLetterEnvelopeQuery { MessageIds = [first.Id] }, CancellationToken.None);

        (await store.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(), CancellationToken.None))
            .TotalCount.ShouldBe(0);
    }

    [Fact]
    public async Task edit_and_replay_updates_every_destination()
    {
        var store = BuildIdAndDestinationStore();
        await store.Admin.RebuildAsync();
        var (first, _) = await DeadLetterSameIdAtTwoDestinations(store);

        (await store.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(), CancellationToken.None))
            .TotalCount.ShouldBe(2);

        var newBody = "updated"u8.ToArray();
        await store.DeadLetters.EditAndReplayAsync(first.Id, newBody, CancellationToken.None);

        var results = await store.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(), CancellationToken.None);
        results.TotalCount.ShouldBe(2);
        foreach (var letter in results.Envelopes)
        {
            letter.Replayable.ShouldBeTrue();
            letter.Envelope.Data.ShouldBe(newBody);
            // The edited body is re-serialized per document, so each keeps its own destination —
            // a single shared blob would collapse both replays onto one inbox identity.
            letter.Envelope.Destination!.ToString().ShouldBe(letter.ReceivedAt);
        }
    }

    [Fact]
    public async Task id_only_mode_writes_the_envelope_guid_as_the_document_id()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("local://dlq-idonly");
        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Inbox.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("boom"));

        // The default mode's _id must stay byte-identical to every previous release: the raw
        // BSON Binary subtype-4 Guid, addressable without any translation.
        var raw = await RawDeadLetters.Find(ById(envelope.Id)).SingleAsync();
        raw["envelopeId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard).ShouldBe(envelope.Id);
    }

    [Fact]
    public async Task migrate_backfills_envelope_id_on_pre_split_documents()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var legacyId = await InsertPreSplitDocument();

        await store.Admin.MigrateAsync();

        var raw = await RawDeadLetters.Find(ById(legacyId)).SingleAsync();
        raw["envelopeId"].AsBsonBinaryData.ToGuid(GuidRepresentation.Standard).ShouldBe(legacyId);
    }

    /// <summary>
    /// Backward-compatibility guard. Documents written before the identity split have no
    /// <c>envelopeId</c> element and stored the envelope Guid directly in <c>_id</c>. This fact
    /// passes both before and after the fix; it exists so that removing the legacy branch from the
    /// Guid-facing filter — while un-migrated documents can still exist — turns it red.
    /// </summary>
    [Fact]
    public async Task legacy_document_without_envelope_id_stays_addressable_by_guid()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        // Deliberately NOT migrated: this is the un-backfilled shape.
        var legacyId = await InsertPreSplitDocument();

        var byId = await store.DeadLetters.DeadLetterEnvelopeByIdAsync(legacyId);
        byId.ShouldNotBeNull();
        byId!.Id.ShouldBe(legacyId);

        (await store.DeadLetters.QueryAsync(
                new DeadLetterEnvelopeQuery { MessageIds = [legacyId] }, CancellationToken.None))
            .TotalCount.ShouldBe(1);

        await store.DeadLetters.EditAndReplayAsync(legacyId, "updated"u8.ToArray(), CancellationToken.None);
        var edited = await RawDeadLetters.Find(ById(legacyId)).SingleAsync();
        edited["replayable"].AsBoolean.ShouldBeTrue();

        await store.DeadLetters.DiscardAsync(
            new DeadLetterEnvelopeQuery { MessageIds = [legacyId] }, CancellationToken.None);
        (await RawDeadLetters.CountDocumentsAsync(new BsonDocument())).ShouldBe(0);
    }

    private IMongoCollection<BsonDocument> RawDeadLetters
        => _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<BsonDocument>(MongoConstants.DeadLetterCollection);

    private static BsonDocument ById(Guid id)
        => new("_id", new BsonBinaryData(id, GuidRepresentation.Standard));

    /// <summary>
    /// Inserts a dead-letter document in the pre-identity-split shape: a Guid <c>_id</c> and no
    /// <c>envelopeId</c> element at all.
    /// </summary>
    private async Task<Guid> InsertPreSplitDocument()
    {
        var legacyId = Guid.NewGuid();
        await RawDeadLetters.InsertOneAsync(new BsonDocument
        {
            { "_id", new BsonBinaryData(legacyId, GuidRepresentation.Standard) },
            { "messageType", "legacy" },
            { "receivedAt", "local://legacy" },
            { "replayable", false },
            { "body", new BsonBinaryData(Array.Empty<byte>()) }
        });
        return legacyId;
    }

    private MongoDbMessageStore BuildIdAndDestinationStore()
    {
        var opts = new WolverineOptions();
        opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;
        return new MongoDbMessageStore(_fixture.Client, AppFixture.DatabaseName, opts);
    }

    /// <summary>
    /// Two deliveries of one envelope Guid to two different destinations — the modular-monolith
    /// scenario MessageIdentity.IdAndDestination exists for — both failing.
    /// </summary>
    private static async Task<(Envelope, Envelope)> DeadLetterSameIdAtTwoDestinations(MongoDbMessageStore store)
    {
        var first = ObjectMother.Envelope();
        first.Destination = DestinationOne;
        first.OwnerId = MongoConstants.AnyNode;
        await store.Inbox.StoreIncomingAsync(first);

        var second = ObjectMother.Envelope();
        second.Id = first.Id;
        second.Destination = DestinationTwo;
        second.OwnerId = MongoConstants.AnyNode;
        await store.Inbox.StoreIncomingAsync(second);

        await store.Inbox.MoveToDeadLetterStorageAsync(first, new InvalidOperationException("boom-one"));
        await store.Inbox.MoveToDeadLetterStorageAsync(second, new InvalidOperationException("boom-two"));

        return (first, second);
    }
}
