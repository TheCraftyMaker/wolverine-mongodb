using System.Collections.Concurrent;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// S11 — the saga behaviours the upstream compliance suite does <b>not</b> cover, proven against a
/// real MongoDB replica set through the actual generated saga frames:
/// <list type="number">
///   <item><b>Atomicity</b> — a saga handler that mutates saga state <i>and</i> cascades an outgoing
///   message commits both together; a forced failure after both are staged rolls back
///   <i>neither</i> the saga document nor the cascaded envelope.</item>
///   <item><b>Completion</b> — <c>MarkCompleted()</c> deletes the saga document.</item>
///   <item><b>Optimistic concurrency</b> — a stale-version update throws
///   <see cref="SagaConcurrencyException"/> without clobbering the winning write. (The frame-level
///   version-stamp/increment contract is additionally proven in
///   <c>saga_optimistic_concurrency.cs</c>, landed with S8; here the <i>winning</i> write goes
///   through the real generated update frame and only the losing writer is simulated.)</item>
///   <item><b>Idempotency</b> — redelivering the <i>same</i> envelope through the durable inbox
///   applies the saga state exactly once.</item>
/// </list>
///
/// Modelled on the demo's <c>OutboxAtomicityTests</c>: rather than racing the outbox relay to read
/// <c>wolverine_outgoing_envelopes</c> directly (which the relay can clear before the assertion
/// runs), atomicity is proven by the observable downstream <i>effect</i> of the cascade. A message
/// published to a durable <b>local</b> queue is persisted — atomically with the saga write — to the
/// incoming store and then delivered to its handler (Wolverine's
/// <c>DurableLocalQueue.storeAndEnqueueAsync</c>), so "the cascade was handled" is an exact proxy for
/// "the outgoing envelope was committed". The failure path additionally asserts directly that no
/// cascaded envelope leaked into either durable store.
/// </summary>
[Collection("mongodb")]
public class saga_atomicity
{
    private readonly AppFixture _fixture;
    public saga_atomicity(AppFixture fixture) => _fixture = fixture;

    private async Task<IHost> BuildHostAsync()
    {
        // Shared fixture database — clear it (incl. the per-saga-type collections) before each build.
        await _fixture.ClearAll();

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Fresh compiled assembly per host (mirrors MongoDbSagaHost): avoids the cross-host
                // in-memory handler reuse that a shared, message-type-keyed Auto codegen name causes.
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

                // Only this file's saga + cascade recorder — never the other test handlers in the
                // assembly (some need a generic document-persistence provider this library does not
                // advertise, so conventional discovery would break codegen).
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(S11AtomicitySaga))
                    .IncludeType(typeof(StockReserverHandler));

                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);

                // Route the cascade + continue messages through durable local queues so the outbox
                // path is exercised without a broker. Durable local-queue sends persist to the
                // INCOMING store (not the outgoing store) on commit, then deliver to the handler.
                opts.LocalQueueFor<ReserveStock>().UseDurableInbox();
                opts.LocalQueueFor<ContinueWork>().UseDurableInbox();

                // A redelivered (duplicate) envelope is detected by the durable inbox and discarded
                // rather than retried — required so the idempotency redelivery does not surface as a
                // tracked exception.
                opts.OnException<DuplicateIncomingEnvelopeException>().Discard();
            }).StartAsync();
    }

    // Reads the saga document straight from MongoDB by its _id, independent of Wolverine.
    private async Task<S11AtomicitySaga> LoadSagaAsync(Guid id)
    {
        var collection = _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<S11AtomicitySaga>(MongoConstants.SagaCollectionName(typeof(S11AtomicitySaga)));
        return await collection.Find(Builders<S11AtomicitySaga>.Filter.Eq("_id", id)).FirstOrDefaultAsync();
    }

    // ── (1) atomicity ─────────────────────────────────────────────────────────

    [Fact]
    public async Task success_path_commits_saga_state_and_cascade_together()
    {
        using var host = await BuildHostAsync();
        var id = Guid.NewGuid();
        StockReserverHandler.Reserved.TryRemove(id, out _);

        // Wait for the whole flow incl. the cascaded ReserveStock so its effect is observable.
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new BeginFulfillment(id, Boom: false));

        // The saga document persisted with its mutated state.
        var saga = await LoadSagaAsync(id);
        saga.ShouldNotBeNull();
        saga.Reserved.ShouldBeTrue();

        // The cascaded outgoing message committed, was relayed and handled exactly once.
        StockReserverHandler.Reserved.TryGetValue(id, out var count).ShouldBeTrue();
        count.ShouldBe(1);
    }

    [Fact]
    public async Task forced_failure_rolls_back_saga_state_and_cascade_together()
    {
        using var host = await BuildHostAsync();
        var id = Guid.NewGuid();
        StockReserverHandler.Reserved.TryRemove(id, out _);

        // The start handler mutates saga state and stages a cascade, then throws *after* both are
        // staged. The throw must abort the transaction so neither is persisted.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            host.MessageBus().InvokeAsync(new BeginFulfillment(id, Boom: true)));

        // Give any (incorrectly) relayed cascade a chance to be processed, so a genuine atomicity
        // violation surfaces instead of being masked by timing.
        await Task.Delay(500);

        // The saga document was never inserted.
        (await LoadSagaAsync(id)).ShouldBeNull();

        // The cascade was never delivered — it rolled back with the saga write.
        StockReserverHandler.Reserved.ContainsKey(id).ShouldBeFalse();

        // ...and nothing leaked into the durable stores: the aborted transaction persisted no
        // cascaded envelope in either the incoming or the outgoing store.
        var store = _fixture.BuildMessageStore();
        (await store.Admin.AllIncomingAsync())
            .ShouldNotContain(e => e.MessageType != null && e.MessageType.Contains(nameof(ReserveStock)));
        (await store.Admin.AllOutgoingAsync())
            .ShouldNotContain(e => e.MessageType != null && e.MessageType.Contains(nameof(ReserveStock)));
    }

    // ── (2) completion ────────────────────────────────────────────────────────

    [Fact]
    public async Task marking_completed_deletes_the_saga_document()
    {
        using var host = await BuildHostAsync();
        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new BeginWork(id));
        (await LoadSagaAsync(id)).ShouldNotBeNull("the saga must exist after it is started");

        await host.InvokeMessageAndWaitAsync(new CompleteWork(id));

        (await LoadSagaAsync(id)).ShouldBeNull("MarkCompleted() must delete the saga document");
    }

    // ── (3) optimistic concurrency ──────────────────────────────────────────────

    [Fact]
    public async Task stale_version_update_throws_and_does_not_clobber()
    {
        using var host = await BuildHostAsync();
        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new BeginWork(id)); // version 1, Applied 0

        var database = _fixture.Client.GetDatabase(AppFixture.DatabaseName);
        using var session = await _fixture.Client.StartSessionAsync();

        // A second writer snapshots the saga at the (soon-to-be-stale) version 1.
        var loser = await MongoSagaOperations.LoadSagaAsync<S11AtomicitySaga, Guid>(database, session, id, default);
        loser.ShouldNotBeNull();
        loser.Version.ShouldBe(1);

        // The winner advances the saga through the REAL generated update frame: version 1 -> 2.
        await host.InvokeMessageAndWaitAsync(new ContinueWork(id));

        var afterWinner = await LoadSagaAsync(id);
        afterWinner.ShouldNotBeNull();
        afterWinner.Version.ShouldBe(2);
        afterWinner.Applied.ShouldBe(1);

        // The loser is still based on version 1: its guarded update matches no document and throws,
        // rather than overwriting the winner.
        loser.Applied = 999;
        await Should.ThrowAsync<SagaConcurrencyException>(() =>
            MongoSagaOperations.UpdateSagaAsync<S11AtomicitySaga, Guid>(database, session, loser, id, default));

        // No clobber: the persisted document is the winner's write, at version 2.
        var persisted = await LoadSagaAsync(id);
        persisted.ShouldNotBeNull();
        persisted.Applied.ShouldBe(1);
        persisted.Version.ShouldBe(2);
    }

    // ── (4) idempotency ─────────────────────────────────────────────────────────

    [Fact]
    public async Task redelivering_the_same_envelope_does_not_double_apply()
    {
        using var host = await BuildHostAsync();
        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new BeginWork(id)); // start: Applied 0, version 1

        // First delivery through the durable local queue: the saga applies it once and the durable
        // inbox keeps a handled marker (Status=Handled, KeepUntil = now + KeepAfterMessageHandling).
        var tracked1 = await host.SendMessageAndWaitAsync(new ContinueWork(id));
        var sentEnvelope = tracked1.Executed.SingleEnvelope<ContinueWork>();

        var afterFirst = await LoadSagaAsync(id);
        afterFirst.ShouldNotBeNull();
        afterFirst.Applied.ShouldBe(1);
        afterFirst.Version.ShouldBe(2);

        // Exactly one ContinueWork inbox record exists after the single delivery.
        var store = _fixture.BuildMessageStore();
        (await store.Admin.AllIncomingAsync())
            .Count(e => e.MessageType != null && e.MessageType.Contains(nameof(ContinueWork)))
            .ShouldBe(1);

        // Redeliver the SAME envelope. A redelivery's very first action in the durable receiver is
        // StoreIncomingAsync (DurableLocalQueue.storeAndEnqueueAsync), keyed on the envelope id; the
        // already-present marker makes it throw DuplicateIncomingEnvelopeException, which the receiver
        // swallows — the saga handler is never reached. Asserting that synchronously here proves the
        // dedup deterministically, with none of the async-receiver timing that would let a redelivery
        // race the handled-marker window.
        await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => store.Inbox.StoreIncomingAsync(sentEnvelope));

        // The rejected redelivery never advanced the saga: applied exactly once, not twice.
        var afterRedelivery = await LoadSagaAsync(id);
        afterRedelivery.ShouldNotBeNull();
        afterRedelivery.Applied.ShouldBe(1);
        afterRedelivery.Version.ShouldBe(2);
    }
}

// ── in-test saga, its messages and the cascade recorder ─────────────────────────

/// <summary>
/// A purpose-built saga for the S11 custom scenarios (the upstream compliance sagas cannot throw or
/// cascade on demand). Saga-id member is <c>Id</c> (Guid). Handler method names follow Wolverine's
/// conventions: <c>Start</c>/<c>Starts</c> begin a saga; <c>Handle</c> continues one.
/// </summary>
public class S11AtomicitySaga : Saga
{
    public Guid Id { get; set; }
    public bool Reserved { get; set; }
    public int Applied { get; set; }

    // Plain start (no cascade) used by the completion / OCC / idempotency scenarios.
    public void Start(BeginWork msg) => Id = msg.Id;

    // Cascading start used by the atomicity scenario: mutates saga state AND publishes an outgoing
    // message into the same outbox transaction, then optionally fails *after* both are staged.
    public async Task Starts(BeginFulfillment msg, IMessageContext context)
    {
        Id = msg.Id;
        Reserved = true;
        await context.PublishAsync(new ReserveStock(msg.Id));
        if (msg.Boom)
        {
            throw new InvalidOperationException("forced post-cascade failure (S11 atomicity)");
        }
    }

    // Continue: bumps the apply counter through the real generated, version-guarded update frame.
    public void Handle(ContinueWork msg) => Applied++;

    // Completion: MarkCompleted() makes SagaChain emit the delete frame so the document is removed.
    public void Handle(CompleteWork msg) => MarkCompleted();
}

public record BeginWork(Guid Id);
public record BeginFulfillment(Guid Id, bool Boom);
public record ContinueWork(Guid SagaId);
public record CompleteWork(Guid SagaId);
public record ReserveStock(Guid OrderId);

/// <summary>
/// Records the order ids whose <see cref="ReserveStock"/> cascade was actually delivered and
/// handled. Keyed by id (tests use unique ids) so the static state cannot bleed across the
/// serialized <c>[Collection("mongodb")]</c> tests.
/// </summary>
public static class StockReserverHandler
{
    public static readonly ConcurrentDictionary<Guid, int> Reserved = new();

    public static void Handle(ReserveStock msg) =>
        Reserved.AddOrUpdate(msg.OrderId, 1, (_, c) => c + 1);
}
