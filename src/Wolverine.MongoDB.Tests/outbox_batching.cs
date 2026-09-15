using System.Collections.Concurrent;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// The outbox contract as of WolverineFx 6.38: <see cref="Envelope.WasPersistedInOutbox"/> is set
/// only after a successful write (GH-4371), and the batch overload
/// <see cref="IMessageOutbox.StoreOutgoingAsync(IReadOnlyList{Envelope}, int)"/> (GH-4319) really batches
/// — one bulk <c>update</c> command per batch, inside one transaction, flags only after the commit.
/// Command monitoring (<see cref="CommandStartedEvent"/>) is the proof that the batch is not a
/// per-envelope loop: the driver emits exactly one <c>update</c> command for the whole bulk.
/// </summary>
[Collection("mongodb")]
public class outbox_batching
{
    private readonly AppFixture _fixture;
    public outbox_batching(AppFixture fixture) => _fixture = fixture;

    private sealed record Probe(MongoDbMessageStore Store, ConcurrentQueue<string> Commands);

    private async Task<Probe> buildProbeAsync()
    {
        await _fixture.ClearAll();
        var settings = MongoClientSettings.FromConnectionString(_fixture.ConnectionString);
        var commands = new ConcurrentQueue<string>();
        settings.ClusterConfigurator = cb => cb.Subscribe<CommandStartedEvent>(e =>
        {
            if (e.CommandName is "update" or "commitTransaction" or "abortTransaction" or "insert")
            {
                commands.Enqueue(e.CommandName);
            }
        });
        var client = new MongoClient(settings);
        return new Probe(new MongoDbMessageStore(client, AppFixture.DatabaseName, new WolverineOptions()), commands);
    }

    private static Envelope outgoing(string destination = "local://outbox-batching")
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri(destination);
        envelope.WasPersistedInOutbox.ShouldBeFalse("precondition");
        return envelope;
    }

    [Fact]
    public async Task single_write_sets_the_flag_only_after_the_document_is_stored()
    {
        var probe = await buildProbeAsync();
        var envelope = outgoing();

        await probe.Store.Outbox.StoreOutgoingAsync(envelope, 7);

        envelope.WasPersistedInOutbox.ShouldBeTrue();
        // Owner 7 is a live node, so recovery must not see it; read the document directly instead.
        (await probe.Store.Outbox.LoadOutgoingAsync(envelope.Destination!)).ShouldBeEmpty();
        var doc = await probe.Store.Outgoing.Find(x => x.Id == envelope.Id).SingleAsync(TestContext.Current.CancellationToken);
        doc.OwnerId.ShouldBe(7);
    }

    [Fact]
    public async Task an_empty_batch_issues_no_command_and_flags_nothing()
    {
        var probe = await buildProbeAsync();
        probe.Commands.Clear();

        await probe.Store.Outbox.StoreOutgoingAsync(Array.Empty<Envelope>(), MongoConstants.AnyNode);

        probe.Commands.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_batch_is_one_bulk_update_inside_one_transaction_and_flags_every_envelope_afterwards()
    {
        var probe = await buildProbeAsync();
        var envelopes = Enumerable.Range(0, 25).Select(_ => outgoing()).ToList();
        probe.Commands.Clear();

        await probe.Store.Outbox.StoreOutgoingAsync(envelopes, MongoConstants.AnyNode);

        var commands = probe.Commands.ToArray();
        commands.Count(x => x == "update").ShouldBe(1,
            "25 upserts must travel as ONE bulk update command, not a per-envelope loop");
        commands.Count(x => x == "commitTransaction").ShouldBe(1);
        envelopes.ShouldAllBe(e => e.WasPersistedInOutbox);

        var loaded = await probe.Store.Outbox.LoadOutgoingAsync(envelopes[0].Destination!);
        loaded.Select(x => x.Id).OrderBy(x => x).ShouldBe(envelopes.Select(x => x.Id).OrderBy(x => x));
        loaded.ShouldAllBe(x => x.OwnerId == MongoConstants.AnyNode);
    }

    [Fact]
    public async Task a_batch_replaces_documents_by_id_like_the_single_write()
    {
        var probe = await buildProbeAsync();
        var envelope = outgoing();
        await probe.Store.Outbox.StoreOutgoingAsync(envelope, MongoConstants.AnyNode);

        // Re-storing the same envelope in a batch under a new owner replaces the document rather
        // than duplicating it.
        var replacement = outgoing();
        await probe.Store.Outbox.StoreOutgoingAsync([envelope, replacement], 42);

        var docs = await probe.Store.Outgoing.Find(FilterDefinition<OutgoingMessage>.Empty)
            .ToListAsync(TestContext.Current.CancellationToken);
        docs.Count.ShouldBe(2);
        docs.Single(x => x.Id == envelope.Id).OwnerId.ShouldBe(42);
        docs.Single(x => x.Id == replacement.Id).OwnerId.ShouldBe(42);
    }

    [Fact]
    public async Task a_failing_batch_persists_nothing_and_flags_nothing()
    {
        var probe = await buildProbeAsync();
        var good = outgoing();
        var poison = outgoing();
        // A body over the 16 MB BSON document limit is rejected by the server inside the transaction,
        // which aborts the whole batch.
        poison.Data = new byte[17 * 1024 * 1024];

        await Should.ThrowAsync<Exception>(() => probe.Store.Outbox.StoreOutgoingAsync([good, poison], MongoConstants.AnyNode));

        good.WasPersistedInOutbox.ShouldBeFalse("nothing was committed, so nothing may report as persisted");
        poison.WasPersistedInOutbox.ShouldBeFalse();
        (await probe.Store.Outbox.LoadOutgoingAsync(good.Destination!)).ShouldBeEmpty(
            "the batch is all-or-nothing: the good envelope must not survive a failed batch");
    }

    [Fact]
    public async Task a_failing_single_write_leaves_the_flag_false()
    {
        var probe = await buildProbeAsync();
        var poison = outgoing();
        poison.Data = new byte[17 * 1024 * 1024];

        await Should.ThrowAsync<Exception>(() => probe.Store.Outbox.StoreOutgoingAsync(poison, MongoConstants.AnyNode));

        poison.WasPersistedInOutbox.ShouldBeFalse();
    }
}
