using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Transports.Tcp;

namespace Wolverine.MongoDB.Tests;

public record MultinodeCounterMessage(Guid Id);

public static class MultinodeCounterHandler
{
    // Shared across both in-proc nodes on purpose: it observes total executions
    // process-wide, so a duplicate cross-node execution is visible as a second entry.
    public static readonly List<Guid> Handled = new();
    private static readonly Lock _lock = new();

    public static void Handle(MultinodeCounterMessage message)
    {
        lock (_lock)
        {
            Handled.Add(message.Id);
        }
    }
}

// Cross-node MESSAGE guarantees (distinct from the leadership-identity compliance suite):
// scheduled exactly-once claim, and dead-node envelope rescue end to end. Two in-process
// Balanced hosts share one MongoDB so they genuinely compete for claims.
[Trait("Category", "multinode")]
[Collection("mongodb")]
public class multinode_end_to_end
{
    private readonly AppFixture _fixture;
    public multinode_end_to_end(AppFixture fixture) => _fixture = fixture;

    // UseTcpForControlEndpoint() grabs its own OS-assigned free port internally
    // (PortFinder.GetAvailablePort), so two in-proc Balanced hosts never collide on
    // the control port and we avoid a manual find-then-bind port race.
    private Task<IHost> StartNode() =>
        Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Durability.ScheduledJobPollingTime = TimeSpan.FromMilliseconds(500);
                opts.Durability.ScheduledJobFirstExecution = TimeSpan.Zero;

                // MongoDB has no native control transport; Balanced nodes coordinate over TCP.
                opts.UseTcpForControlEndpoint();

                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName,
                    mongo => mongo.LockLeaseDuration = TimeSpan.FromSeconds(5));
                opts.Discovery.IncludeType(typeof(MultinodeCounterHandler));
                opts.LocalQueueFor<MultinodeCounterMessage>().UseDurableInbox();
            }).StartAsync();

    [Fact]
    public async Task scheduled_message_executes_exactly_once_across_two_nodes()
    {
        await _fixture.ClearAll();
        MultinodeCounterHandler.Handled.Clear();

        using var nodeA = await StartNode();
        using var nodeB = await StartNode();

        var id = Guid.NewGuid();
        // host.MessageBus() yields a bus bound to a fresh MessageContext — the Wolverine-blessed
        // way to obtain a bus from a host (resolving IMessageBus from the root container misses the
        // per-message context durable scheduling needs). Routed to the durable local queue, the
        // scheduled envelope is persisted in MongoDB, so BOTH nodes compete to claim it.
        var bus = nodeA.MessageBus();
        await bus.ScheduleAsync(new MultinodeCounterMessage(id), DateTimeOffset.UtcNow.AddSeconds(1));

        // Give both nodes' scheduled pollers ample time to compete for the claim.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline && MultinodeCounterHandler.Handled.Count == 0)
        {
            await Task.Delay(250);
        }
        await Task.Delay(2000); // window for an (incorrect) duplicate execution to appear

        MultinodeCounterHandler.Handled.Count(x => x == id).ShouldBe(1,
            "the Scheduled->Incoming CAS claim must make cross-node execution exactly-once");
    }

    [Fact]
    public async Task envelopes_owned_by_a_dead_node_are_rescued_by_a_survivor()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        // Simulate a crashed node: an incoming envelope owned by node number 999
        // with NO corresponding node document.
        var stranded = ObjectMother.Envelope();
        stranded.OwnerId = 999;
        await store.Inbox.StoreIncomingAsync(stranded);

        using var survivor = await StartNode();

        // The survivor's recovery loop must release dead-node ownership.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var all = await store.Admin.AllIncomingAsync();
            if (all.Single(x => x.Id == stranded.Id).OwnerId != 999) break;
            await Task.Delay(250);
        }

        (await store.Admin.AllIncomingAsync()).Single(x => x.Id == stranded.Id)
            .OwnerId.ShouldNotBe(999, "a survivor node must release ownership held by a dead node number");
    }
}
