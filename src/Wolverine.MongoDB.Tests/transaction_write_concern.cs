using System.Collections.Concurrent;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Tracking;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

public record WriteConcernProbeCommand(Guid Id);

public record WriteConcernProbeCascade(Guid Id);

public class WriteConcernProbeDoc
{
    public string Id { get; set; } = string.Empty;
}

public static class WriteConcernProbeHandler
{
    public const string CollectionName = "write_concern_probe_docs";

    // Takes MongoDbUnitOfWork so the transactional frame is applied, and cascades a message so an
    // outbox envelope is written on the SAME session — the exact shape the store's durability
    // promise covers.
    public static async Task<WriteConcernProbeCascade> Handle(WriteConcernProbeCommand cmd,
        MongoDbUnitOfWork uow, CancellationToken ct)
    {
        await uow.Collection<WriteConcernProbeDoc>(CollectionName)
            .InsertOneAsync(new WriteConcernProbeDoc { Id = cmd.Id.ToString() }, ct);

        return new WriteConcernProbeCascade(cmd.Id);
    }
}

public static class WriteConcernProbeCascadeHandler
{
    public static void Handle(WriteConcernProbeCascade message)
    {
    }
}

/// <summary>
/// Every transaction this library opens must restate the store's durability pin.
/// <para>
/// MongoDB <b>discards</b> collection/database-level write concern for operations inside a
/// transaction: the writes themselves are never independently acknowledged, only
/// <c>commitTransaction</c> is, and that command's write concern comes from the transaction
/// options → the session's default transaction options → the consumer's <c>MongoClient</c>
/// settings. So the pin on the store's database handle (<see cref="MongoDbMessageStore"/> ctor:
/// <c>w:majority</c> + <c>j:true</c>, majority reads) buys nothing inside a transaction unless the
/// transaction restates it. <c>durability_concerns.cs</c> asserts those handle settings and stays
/// valid — but it is precisely the assertion that <em>cannot</em> see this, because it inspects the
/// value the driver throws away.
/// </para>
/// <para>
/// OBSERVATION MECHANISM: a per-test <see cref="MongoClient"/> built from the fixture's connection
/// string with a deliberately <b>weak</b> configuration (<see cref="WriteConcern.W1"/>,
/// <see cref="ReadConcern.Local"/>) plus a <see cref="CommandStartedEvent"/> subscriber. The
/// assertions are on the emitted command documents — <c>commitTransaction</c> carries
/// <c>writeConcern</c> verbatim, and the first command of a transaction
/// (<c>startTransaction: true</c>) carries the transaction's <c>readConcern</c>. Explicitly weak
/// (rather than a default client) matters: an unpinned commit then renders <c>{w: 1}</c> instead of
/// omitting the field, so a regression fails with a readable value mismatch instead of a
/// missing-key throw.
/// </para>
/// <para>
/// WHY NOT A FAILOVER TEST: the fixture's replica set is <b>single-node</b> (<c>AppFixture.cs</c>:
/// <c>new MongoDbBuilder("mongo:7").WithReplicaSet()</c>), so <c>w:1</c> and <c>w:majority</c>
/// acknowledge identically and no durability difference is observable behaviourally. The emitted
/// command <em>is</em> the right bar here; do not try to replace these with a failover test on this
/// harness.
/// </para>
/// </summary>
[Collection("mongodb")]
public class transaction_write_concern
{
    private readonly AppFixture _fixture;
    public transaction_write_concern(AppFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A <see cref="CommandStartedEvent"/> reduced to the fields under test, materialised inside the
    /// subscriber. <see cref="CommandStartedEvent.Command"/> is a pooled <c>RawBsonDocument</c> that
    /// is disposed as soon as the command completes, so it cannot be read at assertion time.
    /// </summary>
    private sealed record CapturedCommand(
        string Name,
        string? WriteConcernJson,
        string? ReadConcernJson,
        bool StartsTransaction);

    private sealed record Probe(IMongoClient Client, ConcurrentQueue<CapturedCommand> Events);

    /// <summary>
    /// A client whose own concerns are deliberately weaker than the store's pin, so anything the
    /// library fails to pin shows up as <c>{w: 1}</c> / <c>{level: "local"}</c> on the wire.
    /// </summary>
    private Probe BuildProbeClient()
    {
        var settings = MongoClientSettings.FromConnectionString(_fixture.ConnectionString);
        settings.WriteConcern = WriteConcern.W1;
        settings.ReadConcern = ReadConcern.Local;

        var events = new ConcurrentQueue<CapturedCommand>();
        settings.ClusterConfigurator = cb => cb.Subscribe<CommandStartedEvent>(e =>
        {
            var command = e.Command;
            events.Enqueue(new CapturedCommand(
                e.CommandName,
                command.Contains("writeConcern") ? command["writeConcern"].ToJson() : null,
                command.Contains("readConcern") ? command["readConcern"].ToJson() : null,
                command.Contains("startTransaction")));
        });

        return new Probe(new MongoClient(settings), events);
    }

    private static CapturedCommand[] Commits(Probe probe)
        => probe.Events.ToArray().Where(e => e.Name == "commitTransaction").ToArray();

    private static CapturedCommand[] TransactionFirstCommands(Probe probe)
        => probe.Events.ToArray().Where(e => e.StartsTransaction).ToArray();

    private static void ShouldCommitAtMajorityJournaled(CapturedCommand commit)
    {
        commit.WriteConcernJson.ShouldNotBeNull(
            "commitTransaction carried no writeConcern at all — the transaction inherited the " +
            "consumer's MongoClient default instead of the store's pin");

        var wc = BsonDocument.Parse(commit.WriteConcernJson);

        wc.Contains("w").ShouldBeTrue($"commitTransaction writeConcern had no 'w': {wc}");
        wc["w"].ToString().ShouldBe("majority");

        wc.Contains("j").ShouldBeTrue($"commitTransaction writeConcern had no 'j': {wc}");
        wc["j"].AsBoolean.ShouldBeTrue();
    }

    private static void ShouldReadAtMajority(CapturedCommand first)
    {
        first.ReadConcernJson.ShouldNotBeNull(
            $"the first command of the transaction ({first.Name}) carried no readConcern");

        var rc = BsonDocument.Parse(first.ReadConcernJson);
        rc.Contains("level").ShouldBeTrue($"readConcern had no level: {rc}");
        rc["level"].AsString.ShouldBe("majority");
    }

    private static Envelope freshEnvelope()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("rabbitmq://queue/write-concern-probe");
        return envelope;
    }

    private static async Task<(MongoDbMessageStore Store, Envelope Envelope)> StoredEnvelopeAsync(Probe probe)
    {
        var store = new MongoDbMessageStore(probe.Client, AppFixture.DatabaseName, new WolverineOptions());
        await store.Admin.RebuildAsync();

        var envelope = freshEnvelope();
        await store.Inbox.StoreIncomingAsync(envelope);

        // Setup traffic is not the subject: only what the transaction under test emits is.
        probe.Events.Clear();

        return (store, envelope);
    }

    // ── the two store-owned transactions ────────────────────────────────────────

    [Fact]
    public async Task dead_letter_move_commits_at_majority_journaled()
    {
        var probe = BuildProbeClient();
        var (store, envelope) = await StoredEnvelopeAsync(probe);

        await store.Inbox.MoveToDeadLetterStorageAsync(envelope, new DivideByZeroException("poison"));

        var commits = Commits(probe);
        commits.ShouldNotBeEmpty("the dead-letter move did not open a transaction at all");
        foreach (var commit in commits) ShouldCommitAtMajorityJournaled(commit);

        // Not vacuous: the move really happened. This read goes through the store's majority-read
        // handle, so it also only observes the delete once the transaction is majority-committed.
        (await store.Admin.AllIncomingAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task dead_letter_move_reads_at_majority()
    {
        var probe = BuildProbeClient();
        var (store, envelope) = await StoredEnvelopeAsync(probe);

        await store.Inbox.MoveToDeadLetterStorageAsync(envelope, new DivideByZeroException("poison"));

        var firsts = TransactionFirstCommands(probe);
        firsts.ShouldNotBeEmpty("the dead-letter move did not open a transaction at all");
        foreach (var first in firsts) ShouldReadAtMajority(first);
    }

    /// <summary>
    /// LOCK-IN, not a regression bar: the batch inbox store already carried its options before this
    /// change. It is asserted so the shared-constant / single-funnel refactor cannot silently drop
    /// them from the one site that was already correct.
    /// </summary>
    [Fact]
    public async Task batch_inbox_store_commits_at_majority_journaled_and_reads_at_majority()
    {
        var probe = BuildProbeClient();
        var store = new MongoDbMessageStore(probe.Client, AppFixture.DatabaseName, new WolverineOptions());
        await store.Admin.RebuildAsync();
        probe.Events.Clear();

        var batch = new List<Envelope> { freshEnvelope(), freshEnvelope() };
        await store.Inbox.StoreIncomingAsync(batch);

        var commits = Commits(probe);
        commits.ShouldNotBeEmpty("the batch inbox store did not open a transaction at all");
        foreach (var commit in commits) ShouldCommitAtMajorityJournaled(commit);

        var firsts = TransactionFirstCommands(probe);
        firsts.ShouldNotBeEmpty();
        foreach (var first in firsts) ShouldReadAtMajority(first);

        (await store.Admin.AllIncomingAsync()).Count.ShouldBe(2);
    }

    // ── the code-generated handler/outbox transaction ───────────────────────────

    private async Task<IHost> BuildProbeHostAsync(Probe probe)
    {
        await _fixture.ClearAll();

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Fresh compiled assembly per host so the frame's emitted text is actually
                // re-generated (mirrors entity_atomicity / saga_identity_conventions).
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

                // Keep the host minimal: the assertions below say EVERY transaction opened during
                // this host's lifetime must be pinned, which only stays meaningful while the only
                // transactions are the library's own.
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(WriteConcernProbeHandler))
                    .IncludeType(typeof(WriteConcernProbeCascadeHandler));

                opts.Services.AddSingleton<IMongoClient>(probe.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Policies.AutoApplyTransactions();

                // Durable local queue: the cascade is persisted on the handler's session, so the
                // outbox write really participates in the transaction under test.
                opts.LocalQueueFor<WriteConcernProbeCascade>().UseDurableInbox();
            }).StartAsync();
    }

    private async Task RunProbeHandlerAsync(Probe probe, IHost host)
    {
        var id = Guid.NewGuid();

        probe.Events.Clear();
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new WriteConcernProbeCommand(id));

        // Not vacuous: the transaction actually wrote the domain document. Read it back through the
        // FIXTURE's client so the probe's own event stream stays clean.
        var doc = await _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<WriteConcernProbeDoc>(WriteConcernProbeHandler.CollectionName)
            .Find(Builders<WriteConcernProbeDoc>.Filter.Eq(x => x.Id, id.ToString()))
            .FirstOrDefaultAsync();
        doc.ShouldNotBeNull();
    }

    [Fact]
    public async Task handler_transaction_commits_at_majority_journaled()
    {
        var probe = BuildProbeClient();
        using var host = await BuildProbeHostAsync(probe);

        await RunProbeHandlerAsync(probe, host);

        var commits = Commits(probe);
        commits.ShouldNotBeEmpty("the handler chain did not open a transaction at all");

        // "Every commit" is deliberate: it is simultaneously the frame assertion and the structural
        // guard that no transaction the library opens at runtime escapes the pin.
        foreach (var commit in commits) ShouldCommitAtMajorityJournaled(commit);
    }

    [Fact]
    public async Task handler_transaction_reads_at_majority()
    {
        var probe = BuildProbeClient();
        using var host = await BuildProbeHostAsync(probe);

        await RunProbeHandlerAsync(probe, host);

        var firsts = TransactionFirstCommands(probe);
        firsts.ShouldNotBeEmpty("the handler chain did not open a transaction at all");

        // Majority reads are pinned on the handler transaction too, deliberately: the sessionless
        // ExistsAsync(Envelope, CancellationToken) probe already reads at majority through the
        // pinned database handle, so pinning the in-session eager idempotency probe makes the two
        // idempotency paths consistent rather than introducing a new policy.
        foreach (var first in firsts) ShouldReadAtMajority(first);
    }
}
