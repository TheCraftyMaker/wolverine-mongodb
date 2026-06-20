using System.Collections.Concurrent;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.MongoDB.Internals;
using Wolverine.Transports.Tcp;
using Xunit;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// S14 — cross-node exactly-once saga progression under <see cref="DurabilityMode.Balanced"/>.
///
/// Two in-process Balanced hosts share one MongoDB replica set (mirrors
/// <c>multinode_end_to_end.cs</c>). A single saga is started, then advanced by several continue
/// messages spread <b>deterministically</b> across both nodes, and finally completed. The test
/// asserts the saga advances <b>exactly once per message</b> (no drop, no double-advance) and that
/// completion deletes the document.
///
/// <para><b>Why the distribution is deterministic.</b> A durable local-queue send is persisted with
/// <c>OwnerId = AssignedNodeNumber</c> and enqueued on the <i>sending</i> node's own receiver
/// (<c>DurableLocalQueue.storeAndEnqueueAsync</c>); a live node's owned envelopes are never stolen by
/// the recovery loops. So sending an <see cref="AdvanceFulfillment"/> through node A's bus makes node A
/// process it, and through node B's bus makes node B process it. Alternating the bus per message and
/// firing them concurrently forces <i>both</i> nodes to update the same saga document at the same time
/// — genuine cross-node contention, not a single node doing all the work.</para>
///
/// <para><b>Why a retry policy is mandatory (plan risk R11).</b> Two nodes mutating the same saga
/// document inside concurrent MongoDB transactions cannot both win. The loser surfaces the conflict in
/// one of two ways: usually a server-side <c>WriteConflict</c> carrying the
/// <c>"TransientTransactionError"</c> label (the transaction is fully aborted before commit), or — in
/// the rarer case where the winner has already committed before the loser's guarded
/// <c>ReplaceOneAsync</c> runs — a <see cref="SagaConcurrencyException"/> from the version guard
/// (<c>ModifiedCount == 0</c>). The <see cref="TransactionalFrame"/> uses an explicit
/// start/commit (not Mongo's auto-retrying <c>WithTransaction</c>), so neither conflict is retried for
/// us. The <see cref="DurabilityMode.Balanced"/> error policy below retries <b>both</b>: each is a
/// guaranteed no-commit abort, so the loser safely reloads the now-advanced saga and re-applies its
/// own step. This is the correct production behaviour for a contended saga — not an assertion-weakening
/// bandaid. (Removing it makes the test fail with a <i>dropped</i> step, which is how its necessity was
/// verified.)</para>
///
/// <para><b>Why the oracle is the committed document, not a static counter.</b> Counting handler-body
/// executions in a shared static would over-count: a retried message runs the handler body on every
/// attempt but commits only once. The retry-immune source of truth is the persisted saga state. The
/// saga records each applied <see cref="AdvanceFulfillment.Step"/> in its own <c>AppliedSteps</c> list,
/// so the committed document proves exactly-once directly: the sorted list must equal {1..N} — every
/// step present (no drop) exactly once (no double-advance). The shared static
/// <see cref="S14Observer"/> is used only to confirm <i>both</i> nodes did saga work, so "cross-node"
/// is non-vacuous.</para>
/// </summary>
[Trait("Category", "multinode")]
[Collection("mongodb")]
public class saga_multinode
{
    private const int AdvanceCount = 6; // "several" continue messages; 3 land on each node

    private readonly AppFixture _fixture;
    public saga_multinode(AppFixture fixture) => _fixture = fixture;

    // UseTcpForControlEndpoint() grabs its own OS-assigned free port internally, so two in-proc
    // Balanced hosts never collide on the control port. The node label is OUR identifier (1 or 2),
    // independent of Wolverine's assigned node number, used only to observe which node did work.
    private Task<IHost> StartNode(int nodeLabel) =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;

                // MongoDB has no native control transport; Balanced nodes coordinate over TCP.
                opts.UseTcpForControlEndpoint();

                // Fresh compiled assembly per host (mirrors MongoDbSagaHost / saga_atomicity):
                // avoids cross-host in-memory handler reuse from a message-type-keyed Auto codegen name.
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName,
                    mongo => mongo.LockLeaseDuration = TimeSpan.FromSeconds(5));

                // Only this file's saga — never the other test handlers in the assembly (some need a
                // generic document-persistence provider this library does not advertise, so
                // conventional discovery would break codegen). Mirrors saga_atomicity.
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(S14FulfillmentSaga));

                // Per-node identity so the saga handler can record which node applied each step.
                opts.Services.AddSingleton(new S14NodeMarker(nodeLabel));

                // Route saga messages through durable local queues so they are persisted and the
                // multinode coordination path is exercised. Advances are processed sequentially per
                // node so contention stays pairwise (one A vs one B at a time), keeping the number of
                // OCC retries bounded well within the policy budget.
                opts.LocalQueueFor<StartFulfillment>().UseDurableInbox();
                opts.LocalQueueFor<AdvanceFulfillment>().UseDurableInbox().Sequential();
                opts.LocalQueueFor<FinishFulfillment>().UseDurableInbox();

                // R11: retry a contended saga update so the losing node reloads and re-applies rather
                // than failing. Covers BOTH ways the conflict surfaces (see the class summary). Both
                // are guaranteed no-commit aborts, so the retry cannot double-apply.
                opts.OnException<SagaConcurrencyException>()
                    .Or<MongoException>(ex => ex.HasErrorLabel("TransientTransactionError"))
                    .RetryWithCooldown(
                        TimeSpan.FromMilliseconds(50),
                        TimeSpan.FromMilliseconds(100),
                        TimeSpan.FromMilliseconds(150),
                        TimeSpan.FromMilliseconds(250),
                        TimeSpan.FromMilliseconds(400),
                        TimeSpan.FromMilliseconds(600),
                        TimeSpan.FromMilliseconds(800),
                        TimeSpan.FromSeconds(1));
            }).StartAsync();

    [Fact]
    public async Task saga_advances_exactly_once_across_two_nodes_and_completion_deletes_the_document()
    {
        await _fixture.ClearAll();
        S14Observer.Reset();

        using var nodeA = await StartNode(1);
        using var nodeB = await StartNode(2);

        var id = Guid.NewGuid();
        var busA = nodeA.MessageBus();
        var busB = nodeB.MessageBus();

        // 1) Start the saga on one node and wait until it is durably persisted, so every advance that
        //    follows (on either node) loads an existing saga rather than racing the insert.
        await busA.SendAsync(new StartFulfillment(id));
        var started = await PollSagaAsync(id, s => s is not null, TimeSpan.FromSeconds(30));
        started.ShouldNotBeNull("the saga must be inserted before advances are sent");
        started.Applied.ShouldBe(0);

        // 2) Fire all advances concurrently, alternating the bus per step. Even steps go to node A,
        //    odd steps to node B — both nodes contend for the same saga document at the same time.
        var sends = new List<Task>();
        for (var step = 1; step <= AdvanceCount; step++)
        {
            var bus = step % 2 == 0 ? busA : busB;
            sends.Add(bus.SendAsync(new AdvanceFulfillment(id, step)).AsTask());
        }
        await Task.WhenAll(sends);

        // 3) Wait until every step has been applied, then settle to let any (incorrect) extra
        //    application surface before asserting.
        var advanced = await PollSagaAsync(id,
            s => s is not null && s.AppliedSteps.Count >= AdvanceCount, TimeSpan.FromSeconds(90));
        advanced.ShouldNotBeNull();
        await Task.Delay(2000);

        var saga = await LoadSagaAsync(id);
        saga.ShouldNotBeNull();

        // Exactly-once: every step applied once and only once — no drop, no double-advance.
        saga.AppliedSteps.OrderBy(x => x).ShouldBe(Enumerable.Range(1, AdvanceCount),
            "each advance must be applied exactly once across both nodes (no drop, no double-advance)");
        saga.Applied.ShouldBe(AdvanceCount);

        // Cross-node is non-vacuous: both nodes actually processed saga work.
        S14Observer.ParticipatingNodes.ShouldBe(new[] { 1, 2 }, ignoreOrder: true,
            "both Balanced nodes must process saga messages for this to be a cross-node test");

        // 4) Completion deletes the document.
        await busA.SendAsync(new FinishFulfillment(id));
        var completed = await PollSagaAsync(id, s => s is null, TimeSpan.FromSeconds(30));
        completed.ShouldBeNull("MarkCompleted() must delete the saga document");
    }

    // Reads the saga document straight from MongoDB by its _id, independent of Wolverine.
    private async Task<S14FulfillmentSaga?> LoadSagaAsync(Guid id)
    {
        var collection = _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<S14FulfillmentSaga>(MongoConstants.SagaCollectionName(typeof(S14FulfillmentSaga)));
        return await collection.Find(Builders<S14FulfillmentSaga>.Filter.Eq("_id", id)).FirstOrDefaultAsync();
    }

    private async Task<S14FulfillmentSaga?> PollSagaAsync(
        Guid id, Func<S14FulfillmentSaga?, bool> until, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        S14FulfillmentSaga? saga = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            saga = await LoadSagaAsync(id);
            if (until(saga)) return saga;
            await Task.Delay(200);
        }
        return saga;
    }
}

// ── in-test saga, its messages, the per-node marker and the cross-node observer ──────────────────

/// <summary>
/// Purpose-built saga for the S14 cross-node scenario. Saga-id member is <c>Id</c> (Guid). Each
/// applied step is recorded in <c>AppliedSteps</c> (persisted state) so the committed document is the
/// retry-immune exactly-once oracle.
/// </summary>
public class S14FulfillmentSaga : Saga
{
    public Guid Id { get; set; }
    public int Applied { get; set; }
    public List<int> AppliedSteps { get; set; } = new();

    public void Start(StartFulfillment msg) => Id = msg.Id;

    // Continue: advances the saga through the real generated, version-guarded update frame. The
    // injected marker lets the test confirm both nodes did work. (The static record runs on every
    // attempt incl. retried ones — fine for "which nodes participated", which is why exactly-once is
    // asserted from the persisted AppliedSteps, not from this static.)
    public void Handle(AdvanceFulfillment msg, S14NodeMarker node)
    {
        Applied++;
        AppliedSteps.Add(msg.Step);
        S14Observer.Record(node.Number);
    }

    // Completion: MarkCompleted() makes SagaChain emit the delete frame so the document is removed.
    public void Handle(FinishFulfillment msg) => MarkCompleted();
}

public record StartFulfillment(Guid Id);
public record AdvanceFulfillment(Guid SagaId, int Step);
public record FinishFulfillment(Guid SagaId);

/// <summary>Per-node identity registered as a singleton with a distinct label on each host.</summary>
public record S14NodeMarker(int Number);

/// <summary>Records which node labels applied saga work, to prove the test is genuinely cross-node.</summary>
public static class S14Observer
{
    private static readonly ConcurrentDictionary<int, byte> _nodes = new();

    public static void Record(int nodeLabel) => _nodes[nodeLabel] = 0;

    public static IReadOnlyCollection<int> ParticipatingNodes => _nodes.Keys.ToArray();

    public static void Reset() => _nodes.Clear();
}
