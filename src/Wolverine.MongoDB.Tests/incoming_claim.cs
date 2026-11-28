using System.Collections.Concurrent;
using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver.Core.Events;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// <see cref="MongoDbMessageStore.TryClaimIncomingAsync"/> claims each row with its own compare-and-swap
/// and returns only the envelopes this call won.
/// </summary>
[Collection("mongodb")]
public class incoming_claim
{
    private readonly AppFixture _fixture;
    public incoming_claim(AppFixture fixture) => _fixture = fixture;

    private static Envelope incoming()
    {
        var envelope = ObjectMother.Envelope();
        envelope.Destination = new Uri("local://incoming-claim");
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.OwnerId = MongoConstants.AnyNode;
        return envelope;
    }

    [Fact]
    public async Task a_row_another_node_owns_is_not_returned()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();
        var envelope = incoming();
        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Incoming.UpdateOneAsync(x => x.Id == store.InboxIdentity(envelope),
            Builders<IncomingMessage>.Update.Set(x => x.OwnerId, 7), cancellationToken: TestContext.Current.CancellationToken);

        var claimed = await store.TryClaimIncomingAsync(3, [envelope]);

        claimed.ShouldBeEmpty();
        var doc = await store.Incoming.Find(x => x.Id == store.InboxIdentity(envelope))
            .SingleAsync(TestContext.Current.CancellationToken);
        doc.OwnerId.ShouldBe(7);
    }

    [Theory]
    [InlineData(EnvelopeStatus.Scheduled)]
    [InlineData(EnvelopeStatus.Handled)]
    public async Task a_globally_owned_row_that_is_no_longer_incoming_is_not_returned(EnvelopeStatus status)
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();
        var envelope = incoming();
        await store.Inbox.StoreIncomingAsync(envelope);
        await store.Incoming.UpdateOneAsync(x => x.Id == store.InboxIdentity(envelope),
            Builders<IncomingMessage>.Update.Set(x => x.Status, status), cancellationToken: TestContext.Current.CancellationToken);

        var claimed = await store.TryClaimIncomingAsync(3, [envelope]);

        claimed.ShouldBeEmpty();
        var doc = await store.Incoming.Find(x => x.Id == store.InboxIdentity(envelope))
            .SingleAsync(TestContext.Current.CancellationToken);
        doc.OwnerId.ShouldBe(MongoConstants.AnyNode);
    }

    [Fact]
    public async Task two_claims_for_the_same_rows_split_them()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();
        var all = Enumerable.Range(0, 20).Select(_ => incoming()).ToList();
        foreach (var envelope in all)
        {
            await store.Inbox.StoreIncomingAsync(envelope);
        }

        // Each caller works on its own copies, as two nodes would after loading the same page.
        var pageFor3 = all.Select(copy).ToList();
        var pageFor4 = all.Select(copy).ToList();
        var results = await Task.WhenAll(
            Task.Run(() => store.TryClaimIncomingAsync(3, pageFor3), TestContext.Current.CancellationToken),
            Task.Run(() => store.TryClaimIncomingAsync(4, pageFor4), TestContext.Current.CancellationToken));

        var ids3 = results[0].Select(x => x.Id).ToHashSet();
        var ids4 = results[1].Select(x => x.Id).ToHashSet();
        ids3.Intersect(ids4).ShouldBeEmpty();
        ids3.Union(ids4).ShouldBe(all.Select(x => x.Id).ToHashSet(), ignoreOrder: true);
        results[0].ShouldAllBe(x => x.OwnerId == 3);
        results[1].ShouldAllBe(x => x.OwnerId == 4);

        var docs = await store.Incoming.Find(FilterDefinition<IncomingMessage>.Empty)
            .ToListAsync(TestContext.Current.CancellationToken);
        docs.Count.ShouldBe(20);
        foreach (var doc in docs)
        {
            doc.OwnerId.ShouldBe(ids3.Contains(doc.EnvelopeId) ? 3 : 4);
        }
    }

    [Fact]
    public async Task an_empty_list_issues_no_command()
    {
        await _fixture.ClearAll();
        var settings = MongoClientSettings.FromConnectionString(_fixture.ConnectionString);
        var commands = new ConcurrentQueue<string>();
        settings.ClusterConfigurator = cb => cb.Subscribe<CommandStartedEvent>(e => commands.Enqueue(e.CommandName));
        using var client = new MongoClient(settings);
        var store = new MongoDbMessageStore(client, AppFixture.DatabaseName, new WolverineOptions());
        commands.Clear();

        var claimed = await store.TryClaimIncomingAsync(3, Array.Empty<Envelope>());

        claimed.ShouldBeEmpty();
        commands.Where(x => x == "update").ShouldBeEmpty();
    }

    [Fact]
    public async Task recovery_does_not_claim_a_row_rescheduled_after_the_page_was_read()
    {
        await _fixture.ClearAll();
        const string queue = "claimrace";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobFirstExecution = TimeSpan.FromHours(1);
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.LocalQueue(queue).UseDurableInbox();
            }).StartAsync(TestContext.Current.CancellationToken);

        var envelope = incoming();
        envelope.Destination = new Uri($"local://{queue}");
        var seed = _fixture.BuildMessageStore();
        await seed.Inbox.StoreIncomingAsync(envelope);
        var id = seed.InboxIdentity(envelope);

        // Moves the row back to Scheduled right after the page query returns it, the way a retry reschedule would.
        var settings = MongoClientSettings.FromConnectionString(_fixture.ConnectionString);
        settings.ClusterConfigurator = cb => cb.Subscribe<CommandSucceededEvent>(e =>
        {
            if (e.CommandName == "find")
            {
                _fixture.Client.GetDatabase(AppFixture.DatabaseName).GetCollection<IncomingMessage>(MongoConstants.IncomingCollection)
                    .UpdateOne(x => x.Id == id, Builders<IncomingMessage>.Update.Set(x => x.Status, EnvelopeStatus.Scheduled));
            }
        });
        using var client = new MongoClient(settings);
        var store = new MongoDbMessageStore(client, AppFixture.DatabaseName, new WolverineOptions());

        await store.RecoverOrphanedIncomingAsync(host.GetRuntime(), TestContext.Current.CancellationToken);

        var doc = await store.Incoming.Find(x => x.Id == store.InboxIdentity(envelope))
            .SingleAsync(TestContext.Current.CancellationToken);
        doc.Status.ShouldBe(EnvelopeStatus.Scheduled);
        doc.OwnerId.ShouldBe(MongoConstants.AnyNode);
    }

    private static Envelope copy(Envelope source)
    {
        var envelope = incoming();
        envelope.Id = source.Id;
        envelope.Destination = source.Destination;
        return envelope;
    }
}
