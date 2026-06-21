using System.Collections.Concurrent;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence;
using Wolverine.Transports.Tcp;
using Xunit;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// Cross-node generic-entity persistence under <see cref="DurabilityMode.Balanced"/> — the
/// multinode counterpart to <c>entity_atomicity</c> (which is Solo), symmetric with
/// <c>saga_multinode</c>.
///
/// <para>Two in-process Balanced hosts share one MongoDB replica set (mirrors
/// <c>saga_multinode</c> / <c>multinode_end_to_end.cs</c>). Several <see cref="CreateNote"/> commands
/// are spread <b>deterministically</b> across both nodes; each handler returns
/// <c>Insert&lt;MultinodeNote&gt;</c> <i>and</i> cascades a <see cref="NoteIndexed"/> message through a
/// durable local queue. The test asserts every entity was persisted exactly once on the expected node
/// (no drop, both nodes did entity work) and every cascade was delivered exactly once across the
/// cluster — i.e. the entity frames (<c>LoadEntityFrame</c>/<c>MongoUpsertEntityFrame</c>/the
/// storage-action applier) resolve the transaction session and commit atomically with the outbox on a
/// Balanced node exactly as they do in Solo.</para>
///
/// <para><b>Why the distribution is deterministic.</b> A durable local-queue send is persisted with
/// <c>OwnerId = AssignedNodeNumber</c> and enqueued on the <i>sending</i> node's own receiver
/// (<c>DurableLocalQueue.storeAndEnqueueAsync</c>); a live node's owned envelopes are never stolen by
/// the recovery loops. So a <see cref="CreateNote"/> sent through node A's bus is processed by node A
/// and through node B's bus by node B. Alternating the bus per command forces <i>both</i> nodes to
/// exercise the generic entity write path — not a single node doing all the work.</para>
///
/// <para><b>Why there is no OCC contention here (unlike <c>saga_multinode</c>).</b> Each command
/// targets a <i>distinct</i> entity id, and plain entities are last-write-wins upserts with no
/// optimistic concurrency by design (D6 LD2) — so there is no same-document race to retry. Asserting
/// cross-node accumulation on a <i>single</i> entity would only re-assert documented LWW behaviour and
/// would be non-deterministic; that is deliberately out of scope. The light
/// <c>TransientTransactionError</c> retry below is defensive only (distinct-document transactions do
/// not <c>WriteConflict</c>); a genuinely dropped write still fails the no-drop assertion because the
/// retry-immune oracle is the persisted document, never a handler-body counter.</para>
/// </summary>
[Trait("Category", "multinode")]
[Collection("mongodb")]
public class entity_multinode
{
    private const int CreateCount = 6; // even steps -> node A (label 1), odd steps -> node B (label 2)

    private readonly AppFixture _fixture;
    public entity_multinode(AppFixture fixture) => _fixture = fixture;

    // UseTcpForControlEndpoint() grabs its own OS-assigned free port internally, so two in-proc
    // Balanced hosts never collide on the control port. The node label is OUR identifier (1 or 2),
    // independent of Wolverine's assigned node number, recorded into the entity so the test can prove
    // which node performed each write.
    private Task<IHost> StartNode(int nodeLabel) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;

                // MongoDB has no native control transport; Balanced nodes coordinate over TCP.
                opts.UseTcpForControlEndpoint();

                // Fresh compiled assembly per host (mirrors saga_multinode): avoids cross-host
                // in-memory handler reuse from a message-type-keyed Auto codegen name.
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName,
                    mongo => mongo.LockLeaseDuration = TimeSpan.FromSeconds(5));
                opts.Policies.AutoApplyTransactions();

                // Only this file's handlers — never the other test handlers in the assembly. Mirrors
                // saga_multinode / entity_atomicity.
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(CreateNoteHandler))
                    .IncludeType(typeof(NoteIndexedHandler));

                // Per-node identity so the handler can record which node performed the entity write.
                opts.Services.AddSingleton(new MultinodeNodeMarker(nodeLabel));

                // Route messages through durable local queues so the entity write + outbox commit run
                // through the real multinode coordination path. No .Sequential(): distinct-id writes do
                // not contend, so they can run in parallel.
                opts.LocalQueueFor<CreateNote>().UseDurableInbox();
                opts.LocalQueueFor<NoteIndexed>().UseDurableInbox();

                // Defensive: two nodes running concurrent MongoDB transactions can occasionally surface
                // a server-side WriteConflict carrying the "TransientTransactionError" label even
                // without same-document contention. The TransactionalFrame uses an explicit
                // start/commit (not Mongo's auto-retrying WithTransaction), so retry it here — a
                // guaranteed no-commit abort, so the retry cannot double-write. NOT an
                // assertion-weakening bandaid: a real dropped entity still fails the no-drop assertion.
                opts.OnException<MongoException>(ex => ex.HasErrorLabel("TransientTransactionError"))
                    .RetryWithCooldown(
                        TimeSpan.FromMilliseconds(50),
                        TimeSpan.FromMilliseconds(100),
                        TimeSpan.FromMilliseconds(150),
                        TimeSpan.FromMilliseconds(250),
                        TimeSpan.FromMilliseconds(400));
            }).StartAsync();

    [Fact]
    public async Task entities_persist_exactly_once_across_two_balanced_nodes()
    {
        await _fixture.ClearAll();
        // Entity collections are application-owned, not Wolverine system collections, so ClearAll()
        // does not touch them — drop ours explicitly.
        await Database.DropCollectionAsync(MongoConstants.EntityCollectionName(typeof(MultinodeNote)));
        NoteIndexedHandler.Indexed.Clear();

        using var nodeA = await StartNode(1);
        using var nodeB = await StartNode(2);

        var busA = nodeA.MessageBus();
        var busB = nodeB.MessageBus();

        // Distinct entity id per step so writes never contend (entities are LWW/no-OCC by design).
        var ids = Enumerable.Range(1, CreateCount).ToDictionary(step => step, _ => Guid.NewGuid().ToString());

        // Fire all creates concurrently, alternating the bus per step: even steps land on node A,
        // odd steps on node B — both nodes exercise the generic entity write path at the same time.
        var sends = new List<Task>();
        for (var step = 1; step <= CreateCount; step++)
        {
            var bus = step % 2 == 0 ? busA : busB;
            sends.Add(bus.SendAsync(new CreateNote(ids[step], step)).AsTask());
        }
        await Task.WhenAll(sends);

        // Wait until every entity has been persisted, then settle to let any (incorrect) extra write
        // surface before asserting.
        var allPresent = await PollAsync(
            async () => await CountNotesAsync() >= CreateCount, TimeSpan.FromSeconds(90));
        allPresent.ShouldBeTrue("every CreateNote must persist its entity (no drop) across both nodes");
        await Task.Delay(2000);

        // No drop + correct content + deterministic node attribution: each entity exists exactly once,
        // records its step, and carries the label of the node it was deterministically routed to.
        (await CountNotesAsync()).ShouldBe(CreateCount, "no extra/duplicate entity documents");
        for (var step = 1; step <= CreateCount; step++)
        {
            var expectedNode = step % 2 == 0 ? 1 : 2;
            var note = await LoadNoteAsync(ids[step]);
            note.ShouldNotBeNull($"the entity for step {step} must be persisted");
            note.Step.ShouldBe(step);
            note.NodeLabel.ShouldBe(expectedNode,
                $"step {step} was routed to node {expectedNode}, which must have performed the entity write");
        }

        // Cross-node is non-vacuous: both Balanced nodes actually performed entity writes.
        var participatingNodes = (await AllNotesAsync()).Select(n => n.NodeLabel).Distinct().ToList();
        participatingNodes.ShouldBe(new[] { 1, 2 }, ignoreOrder: true,
            "both Balanced nodes must perform entity writes for this to be a cross-node test");

        // Each entity-driven cascade was delivered exactly once across the cluster — the entity write
        // and its outbox cascade committed atomically on the processing node, and the durable inbox
        // deduplicated delivery.
        var allIndexed = await PollAsync(
            () => Task.FromResult(ids.Values.All(id => NoteIndexedHandler.Indexed.ContainsKey(id))),
            TimeSpan.FromSeconds(30));
        allIndexed.ShouldBeTrue("every NoteIndexed cascade must be delivered");
        foreach (var id in ids.Values)
        {
            NoteIndexedHandler.Indexed[id].ShouldBe(1, "each cascade must be delivered exactly once");
        }
    }

    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    private IMongoCollection<MultinodeNote> Notes
        => Database.GetCollection<MultinodeNote>(MongoConstants.EntityCollectionName(typeof(MultinodeNote)));

    private Task<MultinodeNote> LoadNoteAsync(string id)
        => Notes.Find(Builders<MultinodeNote>.Filter.Eq("_id", id)).FirstOrDefaultAsync();

    private Task<long> CountNotesAsync()
        => Notes.CountDocumentsAsync(Builders<MultinodeNote>.Filter.Empty);

    private async Task<List<MultinodeNote>> AllNotesAsync()
        => await (await Notes.FindAsync(Builders<MultinodeNote>.Filter.Empty)).ToListAsync();

    private static async Task<bool> PollAsync(Func<Task<bool>> until, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await until()) return true;
            await Task.Delay(200);
        }
        return false;
    }
}

// ── in-test entity, its messages, the per-node marker and handlers ───────────────────────────────

/// <summary>
/// A plain (non-saga) entity persisted through the generic <c>Insert&lt;T&gt;</c> surface under a
/// Balanced cluster. Id member is <c>Id</c> (string) — the driver's default convention maps it to
/// <c>_id</c>. Records the step and the label of the node that wrote it, so the committed document is
/// the retry-immune oracle for "no drop, written by the expected node".
/// </summary>
public class MultinodeNote
{
    public string Id { get; set; } = null!;
    public int Step { get; set; }
    public int NodeLabel { get; set; }
}

public record CreateNote(string Id, int Step);
public record NoteIndexed(string NoteId);

/// <summary>Per-node identity registered as a singleton with a distinct label on each host.</summary>
public record MultinodeNodeMarker(int Number);

/// <summary>
/// Returns <c>Insert&lt;MultinodeNote&gt;</c> (stamping the processing node's label) AND cascades a
/// <see cref="NoteIndexed"/> message into the same outbox transaction — so a successful run commits the
/// entity write and the cascade atomically on whichever Balanced node processed the command.
/// </summary>
public static class CreateNoteHandler
{
    public static async Task<Insert<MultinodeNote>> Handle(
        CreateNote msg, MultinodeNodeMarker node, IMessageContext context)
    {
        await context.PublishAsync(new NoteIndexed(msg.Id));
        return Storage.Insert(new MultinodeNote { Id = msg.Id, Step = msg.Step, NodeLabel = node.Number });
    }
}

/// <summary>
/// Records how many times each note's <see cref="NoteIndexed"/> cascade was delivered, keyed by note id
/// (commands use unique ids) so the static state cannot bleed across the serialized
/// <c>[Collection("mongodb")]</c> tests. Exactly-once delivery (count == 1) is retry-immune here: a
/// cascade is published inside the entity's transaction, so a no-commit abort rolls the publish back,
/// and the durable inbox deduplicates a committed cascade.
/// </summary>
public static class NoteIndexedHandler
{
    public static readonly ConcurrentDictionary<string, int> Indexed = new();

    public static void Handle(NoteIndexed msg) =>
        Indexed.AddOrUpdate(msg.NoteId, 1, (_, c) => c + 1);
}
