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
        var store = new MongoDbMessageStore(new MongoClient(settings), AppFixture.DatabaseName, new WolverineOptions());
        commands.Clear();

        var claimed = await store.TryClaimIncomingAsync(3, Array.Empty<Envelope>());

        claimed.ShouldBeEmpty();
        commands.Where(x => x == "update").ShouldBeEmpty();
    }

    private static Envelope copy(Envelope source)
    {
        var envelope = incoming();
        envelope.Id = source.Id;
        envelope.Destination = source.Destination;
        return envelope;
    }
}
