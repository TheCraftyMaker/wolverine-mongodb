using System.Collections.Concurrent;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// T1.2 — the generic-entity behaviours the upstream <c>StorageActionCompliance</c> suite does
/// <b>not</b> cover, proven against a real MongoDB replica set through the actual generated entity
/// frames:
/// <list type="number">
///   <item><b>Entity atomicity</b> — a handler that returns <c>Insert&lt;T&gt;</c> <i>and</i> cascades
///   an outgoing message commits both together; a forced failure after both are staged rolls back
///   <i>neither</i> the entity document nor the cascaded envelope.</item>
///   <item><b>Saga/entity coexistence</b> — a single saga handler that mutates saga state <i>and</i>
///   returns <c>Store&lt;T&gt;</c> for a plain entity: the saga keeps its version-stamped,
///   optimistic-concurrency update frame while the entity gets the unversioned upsert frame, proving
///   the saga-vs-entity frame branching (T1.1) did not cross wires.</item>
///   <item><b>Required <c>[Entity]</c> not found</b> — a handler with a required <c>[Entity]</c>
///   for a missing id does not execute (core behaviour, asserted end-to-end through the Mongo load
///   frame).</item>
/// </list>
///
/// Atomicity is proven the same deterministic way as <c>saga_atomicity</c> (S11): rather than racing
/// the outbox relay to read <c>wolverine_outgoing_envelopes</c> directly (which the relay can clear
/// before the assertion runs), it is proven by the observable downstream <i>effect</i> of the cascade.
/// A message published to a durable <b>local</b> queue is persisted — atomically with the entity write
/// — to the incoming store and then delivered to its handler (Wolverine's
/// <c>DurableLocalQueue.storeAndEnqueueAsync</c>), so "the cascade was handled" is an exact proxy for
/// "the outgoing envelope was committed". The failure path additionally asserts directly that no
/// cascaded envelope leaked into either durable store.
/// </summary>
[Collection("mongodb")]
public class entity_atomicity
{
    private readonly AppFixture _fixture;
    public entity_atomicity(AppFixture fixture) => _fixture = fixture;

    private async Task<IHost> BuildHostAsync()
    {
        // Shared fixture database — clear the Wolverine system + saga collections before each build.
        await _fixture.ClearAll();

        // Entity collections are application-owned state, NOT Wolverine system collections, so
        // ClearAll()/RebuildAsync() deliberately does not touch them (D6 Decision 4c). Drop them here
        // so each test starts from a clean slate; the first write inside a transaction recreates them.
        await DropEntityCollectionsAsync();

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Fresh compiled assembly per host (mirrors MongoDbSagaHost / saga_atomicity): avoids
                // the cross-host in-memory handler reuse that a shared, message-type-keyed Auto codegen
                // name causes.
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

                // Only this file's handlers — never the other test handlers in the assembly.
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(EntityAtomicityHandler))
                    .IncludeType(typeof(NoteCascadeHandler))
                    .IncludeType(typeof(CoexistenceSaga))
                    .IncludeType(typeof(RequiredEntityHandler));

                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Policies.AutoApplyTransactions();

                // Route the cascade through a durable local queue so the outbox path is exercised
                // without a broker. Durable local-queue sends persist to the INCOMING store (not the
                // outgoing store) on commit, then deliver to the handler.
                opts.LocalQueueFor<ReserveStockForNote>().UseDurableInbox();
            }).StartAsync();
    }

    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    private Task DropEntityCollectionsAsync() => Task.WhenAll(
        Database.DropCollectionAsync(MongoConstants.EntityCollectionName(typeof(NoteEntity))),
        Database.DropCollectionAsync(MongoConstants.EntityCollectionName(typeof(CoexistEntity))));

    // Reads an entity document straight from MongoDB by its _id, independent of Wolverine. Targets the
    // SAME collection the generated frames write to — MongoConstants.EntityCollectionName — never a
    // hard-coded literal, so the read and write sides stay coupled.
    private Task<NoteEntity> LoadNoteAsync(string id)
        => Database.GetCollection<NoteEntity>(MongoConstants.EntityCollectionName(typeof(NoteEntity)))
            .Find(Builders<NoteEntity>.Filter.Eq("_id", id)).FirstOrDefaultAsync();

    private Task<CoexistEntity> LoadCoexistEntityAsync(string id)
        => Database.GetCollection<CoexistEntity>(MongoConstants.EntityCollectionName(typeof(CoexistEntity)))
            .Find(Builders<CoexistEntity>.Filter.Eq("_id", id)).FirstOrDefaultAsync();

    private Task<CoexistenceSaga> LoadCoexistSagaAsync(Guid id)
        => Database.GetCollection<CoexistenceSaga>(MongoConstants.SagaCollectionName(typeof(CoexistenceSaga)))
            .Find(Builders<CoexistenceSaga>.Filter.Eq("_id", id)).FirstOrDefaultAsync();

    // ── (1) entity atomicity ────────────────────────────────────────────────────

    [Fact]
    public async Task success_path_commits_entity_and_cascade_together()
    {
        using var host = await BuildHostAsync();
        var id = Guid.NewGuid().ToString();
        NoteCascadeHandler.Reserved.TryRemove(id, out _);

        // Wait for the whole flow incl. the cascaded ReserveStockForNote so its effect is observable.
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new BeginNoteFlow(id, Boom: false));

        // The entity document persisted via the generated upsert frame.
        var note = await LoadNoteAsync(id);
        note.ShouldNotBeNull();
        note.Text.ShouldBe("reserved");

        // The cascaded outgoing message committed, was relayed and handled exactly once.
        NoteCascadeHandler.Reserved.TryGetValue(id, out var count).ShouldBeTrue();
        count.ShouldBe(1);
    }

    [Fact]
    public async Task forced_failure_rolls_back_entity_and_cascade_together()
    {
        using var host = await BuildHostAsync();
        var id = Guid.NewGuid().ToString();
        NoteCascadeHandler.Reserved.TryRemove(id, out _);

        // The handler stages a cascade and then throws *before* returning its Insert<NoteEntity>, so
        // the throw aborts the transaction: neither the entity write nor the cascade may be committed.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            host.MessageBus().InvokeAsync(new BeginNoteFlow(id, Boom: true)));

        // Give any (incorrectly) relayed cascade a chance to be processed, so a genuine atomicity
        // violation surfaces instead of being masked by timing.
        await Task.Delay(500);

        // The entity document was never inserted.
        (await LoadNoteAsync(id)).ShouldBeNull();

        // The cascade was never delivered — it rolled back with the entity write.
        NoteCascadeHandler.Reserved.ContainsKey(id).ShouldBeFalse();

        // ...and nothing leaked into the durable stores: the aborted transaction persisted no
        // cascaded envelope in either the incoming or the outgoing store.
        var store = _fixture.BuildMessageStore();
        (await store.Admin.AllIncomingAsync())
            .ShouldNotContain(e => e.MessageType != null && e.MessageType.Contains(nameof(ReserveStockForNote)));
        (await store.Admin.AllOutgoingAsync())
            .ShouldNotContain(e => e.MessageType != null && e.MessageType.Contains(nameof(ReserveStockForNote)));
    }

    // ── (2) saga/entity coexistence regression ──────────────────────────────────

    [Fact]
    public async Task saga_and_entity_writes_coexist_without_crossing_wires()
    {
        using var host = await BuildHostAsync();
        var sagaId = Guid.NewGuid();
        var entityId = Guid.NewGuid().ToString();

        // Start the saga: inserted, Version stamped to 1, Touched 0.
        await host.InvokeMessageAndWaitAsync(new BeginCoexistence(sagaId));
        var afterStart = await LoadCoexistSagaAsync(sagaId);
        afterStart.ShouldNotBeNull();
        afterStart.Version.ShouldBe(1);
        afterStart.Touched.ShouldBe(0);

        // Snapshot a losing writer at version 1 BEFORE the touch advances the saga (deterministic OCC
        // proof, exactly as saga_atomicity does — no async-receiver race).
        using var session = await _fixture.Client.StartSessionAsync();
        var loser = await MongoSagaOperations.LoadSagaAsync<CoexistenceSaga, Guid>(Database, session, sagaId, default);
        loser.ShouldNotBeNull();
        loser.Version.ShouldBe(1);

        // The SINGLE coexistence handler advances the saga (v1 -> v2 through the REAL version-guarded
        // update frame) AND returns Store<CoexistEntity> for a plain entity — proving the branched
        // factories emit BOTH the saga update frame and the entity upsert frame in one chain.
        await host.InvokeMessageAndWaitAsync(new TouchCoexistence(sagaId, entityId, "from-saga"));

        // The saga went through the version-guarded UPDATE frame (Version incremented), NOT an
        // unversioned entity upsert (which would have left Version at 1).
        var afterTouch = await LoadCoexistSagaAsync(sagaId);
        afterTouch.ShouldNotBeNull();
        afterTouch.Version.ShouldBe(2);
        afterTouch.Touched.ShouldBe(1);

        // The entity went through the entity upsert frame and persisted to its own (un-prefixed)
        // collection.
        var entity = await LoadCoexistEntityAsync(entityId);
        entity.ShouldNotBeNull("the Store<CoexistEntity> from the saga handler must persist the entity");
        entity.Note.ShouldBe("from-saga");

        // The saga branch is still the OCC update, not a clobbering upsert: the v1 snapshot's guarded
        // update now matches no document (the winner advanced it to v2) and throws SagaConcurrencyException
        // rather than overwriting the winner.
        loser.Touched = 999;
        await Should.ThrowAsync<SagaConcurrencyException>(() =>
            MongoSagaOperations.UpdateSagaAsync<CoexistenceSaga, Guid>(Database, session, loser, sagaId, default));

        // No clobber: the persisted saga is the winner's write, at version 2.
        var persisted = await LoadCoexistSagaAsync(sagaId);
        persisted.ShouldNotBeNull();
        persisted.Version.ShouldBe(2);
        persisted.Touched.ShouldBe(1);
    }

    // ── (3) required [Entity] not found ─────────────────────────────────────────

    [Fact]
    public async Task required_entity_attribute_skips_handler_when_not_found()
    {
        using var host = await BuildHostAsync();

        var missingId = Guid.NewGuid().ToString();
        RequiredEntityHandler.Executed.TryRemove(missingId, out _);

        // Required [Entity] for a missing id: core short-circuits the handler end-to-end through the
        // Mongo load frame (which returns null) — the handler must NOT execute.
        await host.InvokeMessageAndWaitAsync(new TouchNote(missingId));
        RequiredEntityHandler.Executed.ContainsKey(missingId).ShouldBeFalse();

        // Positive control: when the entity DOES exist, the same handler executes — so the negative
        // result above is the missing entity short-circuiting, not a misrouted/never-handled message.
        var existingId = Guid.NewGuid().ToString();
        RequiredEntityHandler.Executed.TryRemove(existingId, out _);
        await Database.GetCollection<NoteEntity>(MongoConstants.EntityCollectionName(typeof(NoteEntity)))
            .ReplaceOneAsync(
                Builders<NoteEntity>.Filter.Eq("_id", existingId),
                new NoteEntity { Id = existingId, Text = "exists" },
                new ReplaceOptions { IsUpsert = true });

        await host.InvokeMessageAndWaitAsync(new TouchNote(existingId));
        RequiredEntityHandler.Executed.TryGetValue(existingId, out var count).ShouldBeTrue();
        count.ShouldBe(1);
    }
}

// ── in-test entities, sagas, messages and handlers ──────────────────────────────

/// <summary>A plain (non-saga) entity persisted through the generic <c>[Entity]</c>/storage-action
/// surface. Id member is <c>Id</c> (string) — the driver's default convention maps it to <c>_id</c>,
/// exactly like the compliance suite's <c>Todo</c>.</summary>
public class NoteEntity
{
    public string Id { get; set; } = null!;
    public string? Text { get; set; }
}

/// <summary>The plain entity written alongside saga state in the coexistence regression.</summary>
public class CoexistEntity
{
    public string Id { get; set; } = null!;
    public string? Note { get; set; }
}

public record BeginNoteFlow(string Id, bool Boom);
public record ReserveStockForNote(string NoteId);
public record TouchNote(string Id);

/// <summary>
/// Returns <c>Insert&lt;NoteEntity&gt;</c> AND cascades an outgoing message into the same outbox
/// transaction, then optionally throws *after* both are staged (the deterministic S11 technique). The
/// throw propagates before the generated upsert + commit frames run, so the <c>TransactionalFrame</c>
/// aborts the transaction and persists neither the entity nor the cascaded envelope.
/// </summary>
public static class EntityAtomicityHandler
{
    public static async Task<Insert<NoteEntity>> Handle(BeginNoteFlow msg, IMessageContext context)
    {
        await context.PublishAsync(new ReserveStockForNote(msg.Id));
        if (msg.Boom)
        {
            throw new InvalidOperationException("forced post-staging failure (entity atomicity)");
        }

        return Storage.Insert(new NoteEntity { Id = msg.Id, Text = "reserved" });
    }
}

/// <summary>
/// Records the note ids whose <see cref="ReserveStockForNote"/> cascade was actually delivered and
/// handled. Keyed by id (tests use unique ids) so the static state cannot bleed across the serialized
/// <c>[Collection("mongodb")]</c> tests.
/// </summary>
public static class NoteCascadeHandler
{
    public static readonly ConcurrentDictionary<string, int> Reserved = new();

    public static void Handle(ReserveStockForNote msg) =>
        Reserved.AddOrUpdate(msg.NoteId, 1, (_, c) => c + 1);
}

/// <summary>
/// A required <c>[Entity]</c> handler used for the not-found scenario. Records execution by message id
/// so a test can assert the handler did (or did not) run. Would also throw if ever handed a null entity,
/// belt-and-braces with the explicit execution assertion.
/// </summary>
public static class RequiredEntityHandler
{
    public static readonly ConcurrentDictionary<string, int> Executed = new();

    public static void Handle(TouchNote msg, [Entity] NoteEntity note)
    {
        if (note is null)
        {
            throw new ArgumentNullException(nameof(note));
        }

        Executed.AddOrUpdate(msg.Id, 1, (_, c) => c + 1);
    }
}

public record BeginCoexistence(Guid Id);
public record TouchCoexistence(Guid Id, string EntityId, string Note);

/// <summary>
/// A saga whose continue handler writes saga state AND a plain entity in a single handler — the
/// coexistence regression. Saga-id member is <c>Id</c> (Guid). The saga keeps the version-stamped,
/// OCC-guarded update frame; the returned <c>Store&lt;CoexistEntity&gt;</c> takes the unversioned
/// entity upsert frame.
/// </summary>
public class CoexistenceSaga : Saga
{
    public Guid Id { get; set; }
    public int Touched { get; set; }

    public void Start(BeginCoexistence msg) => Id = msg.Id;

    public Store<CoexistEntity> Handle(TouchCoexistence msg)
    {
        Touched++;
        return Storage.Store(new CoexistEntity { Id = msg.EntityId, Note = msg.Note });
    }
}
