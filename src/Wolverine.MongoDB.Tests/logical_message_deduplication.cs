using System.Collections.Concurrent;
using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// GH-4180 logical message deduplication against MongoDB, end to end. The first block mirrors the
/// upstream PostgreSQL suite (there is no shared compliance base class for this feature): whole
/// messages go through the bus so that the generated handler resolving, claiming and stopping on the
/// id is what gets proven. The second block is MongoDB-specific: the claim is a single insert on the
/// <c>_id</c> uniqueness, an expired claim is taken over atomically, a <em>transactional</em> handler
/// that rolls back releases its claim (the TransactionalFrame compensation), and a host without the
/// opt-in gets the null store and no collection.
/// </summary>
[Collection("mongodb")]
public class logical_message_deduplication
{
    private readonly AppFixture _fixture;
    public logical_message_deduplication(AppFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    private async Task<IHost> buildHostAsync(bool enabled = true)
    {
        await _fixture.ClearAll();
        await Database.DropCollectionAsync(MongoConstants.DeduplicationCollection, Ct);
        await Database.DropCollectionAsync(DedupDoc.Collection, Ct);
        DeduplicatedHandler.Received.Clear();
        UnkeyedHandler.Received.Clear();
        DerivedIdentityHandler.Received.Clear();
        FailingDeduplicatedHandler.Attempts = 0;
        FailingTransactionalHandler.Attempts = 0;

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(DeduplicatedHandler))
                    .IncludeType(typeof(DerivedIdentityHandler))
                    .IncludeType(typeof(UnkeyedHandler))
                    .IncludeType(typeof(FailingDeduplicatedHandler))
                    .IncludeType(typeof(FailingTransactionalHandler));
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Durability.EnableMessageDeduplication = enabled;
                opts.Durability.DeduplicationWindow = 1.Hours();
                opts.Policies.AutoApplyTransactions();
                opts.MessageDeduplication.ByMessage<ComposedIdentityMessage>(x => $"{x.Tenant}|{x.Sequence}");
                // Discard rather than retry, so each Send is exactly one handler attempt.
                opts.OnException<DivideByZeroException>().Discard();
            }).StartAsync(Ct);
    }

    private static IDeduplicationStore storeOf(IHost host) => host.GetRuntime().Storage.Deduplication;

    private static string generatedSourceFor(IHost host, Type messageType)
    {
        var graph = host.Services.GetRequiredService<HandlerGraph>();
        graph.HandlerFor(messageType);
        var chain = graph.ChainFor(messageType)!;
        var property = typeof(HandlerChain).GetProperty("SourceCode",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
        return (string)property.GetValue(chain)!;
    }

    // ---- the upstream (PostgreSQL) shapes ----

    [Fact]
    public async Task provisions_the_deduplication_collection_with_a_ttl_index_when_enabled()
    {
        using var host = await buildHostAsync();
        await host.ResetResourceState(Ct);

        storeOf(host).ShouldBeOfType<MongoDbDeduplicationStore>();
        var indexes = await (await Database.GetCollection<BsonDocument>(MongoConstants.DeduplicationCollection)
            .Indexes.ListAsync(Ct)).ToListAsync(Ct);
        var ttl = indexes.SingleOrDefault(x => x["key"].AsBsonDocument.Contains("expires"));
        ttl.ShouldNotBeNull("a TTL index on `expires` is how the server reaps stale claims");
        ttl["expireAfterSeconds"].ToInt64().ShouldBe(0);
    }

    [Fact]
    public async Task second_message_with_the_same_logical_id_is_discarded()
    {
        using var host = await buildHostAsync();
        await host.SendMessageAndWaitAsync(new DeduplicatedMessage("first"),
            new DeliveryOptions { DeduplicationId = "schedule-1|2026-08-29T03:00:00Z" });
        // Same logical id, DIFFERENT payload and a different Envelope.Id — only the logical id can be
        // doing the work here.
        await host.SendMessageAndWaitAsync(new DeduplicatedMessage("second"),
            new DeliveryOptions { DeduplicationId = "schedule-1|2026-08-29T03:00:00Z" });

        DeduplicatedHandler.Received.ShouldHaveSingleItem().ShouldBe("first");
    }

    [Fact]
    public async Task different_logical_ids_both_run()
    {
        using var host = await buildHostAsync();
        await host.SendMessageAndWaitAsync(new DeduplicatedMessage("a"),
            new DeliveryOptions { DeduplicationId = "schedule-1|2026-08-29T03:00:00Z" });
        await host.SendMessageAndWaitAsync(new DeduplicatedMessage("b"),
            new DeliveryOptions { DeduplicationId = "schedule-1|2026-08-30T03:00:00Z" });

        DeduplicatedHandler.Received.ToArray().ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task a_message_with_no_logical_id_is_unaffected_when_the_id_is_optional()
    {
        using var host = await buildHostAsync();
        await host.SendMessageAndWaitAsync(new UnkeyedMessage("x"));
        await host.SendMessageAndWaitAsync(new UnkeyedMessage("y"));

        UnkeyedHandler.Received.ToArray().ShouldBe(["x", "y"]);
    }

    [Fact]
    public async Task a_missing_but_required_logical_id_throws_rather_than_discarding()
    {
        using var host = await buildHostAsync();
        var session = await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new DeduplicatedMessage("no id"));

        session.AllExceptions().OfType<MissingDeduplicationIdException>().ShouldNotBeEmpty();
        DeduplicatedHandler.Received.ShouldBeEmpty();
    }

    [Fact]
    public async Task a_failed_non_transactional_execution_does_not_poison_the_logical_id()
    {
        using var host = await buildHostAsync();
        await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingDeduplicatedMessage(), new DeliveryOptions { DeduplicationId = "poison-check" });
        FailingDeduplicatedHandler.Attempts.ShouldBe(1);

        // Core's own compensating release frame (emitted for non-transactional chains) removed the claim.
        await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingDeduplicatedMessage(), new DeliveryOptions { DeduplicationId = "poison-check" });
        FailingDeduplicatedHandler.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task an_id_derived_from_a_marked_member_is_enforced()
    {
        using var host = await buildHostAsync();
        await host.SendMessageAndWaitAsync(new MarkedIdentityMessage("invoice-17", "first"));
        await host.SendMessageAndWaitAsync(new MarkedIdentityMessage("invoice-17", "second"));
        await host.SendMessageAndWaitAsync(new MarkedIdentityMessage("invoice-18", "third"));

        DerivedIdentityHandler.Received.ToArray().ShouldBe(["first", "third"]);
    }

    [Fact]
    public async Task an_id_derived_from_a_configured_lambda_is_enforced()
    {
        using var host = await buildHostAsync();
        await host.SendMessageAndWaitAsync(new ComposedIdentityMessage("acme", 1, "first"));
        await host.SendMessageAndWaitAsync(new ComposedIdentityMessage("acme", 1, "second"));
        await host.SendMessageAndWaitAsync(new ComposedIdentityMessage("globex", 1, "third"));

        DerivedIdentityHandler.Received.ToArray().ShouldBe(["first", "third"]);
    }

    [Fact]
    public async Task an_explicit_delivery_option_still_wins_over_the_derived_id()
    {
        using var host = await buildHostAsync();
        await host.SendMessageAndWaitAsync(new MarkedIdentityMessage("invoice-19", "first"),
            new DeliveryOptions { DeduplicationId = "override" });
        await host.SendMessageAndWaitAsync(new MarkedIdentityMessage("invoice-20", "second"),
            new DeliveryOptions { DeduplicationId = "override" });

        DerivedIdentityHandler.Received.ShouldHaveSingleItem().ShouldBe("first");
    }

    [Fact]
    public async Task the_reaper_deletes_expired_claims_and_reports_how_many()
    {
        using var host = await buildHostAsync();
        var store = storeOf(host);
        await store.TryClaimAsync("expired-1", DateTimeOffset.UtcNow.Subtract(1.Hours()), Ct);
        await store.TryClaimAsync("expired-2", DateTimeOffset.UtcNow.Subtract(1.Hours()), Ct);
        await store.TryClaimAsync("still-live", DateTimeOffset.UtcNow.Add(1.Hours()), Ct);

        var deleted = await store.DeleteExpiredAsync(DateTimeOffset.UtcNow, Ct);
        deleted.ShouldBe(2);

        (await store.TryClaimAsync("still-live", DateTimeOffset.UtcNow.Add(1.Hours()), Ct)).ShouldBeFalse();
        (await store.TryClaimAsync("expired-1", DateTimeOffset.UtcNow.Add(1.Hours()), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task concurrent_claims_of_the_same_id_produce_exactly_one_winner()
    {
        using var host = await buildHostAsync();
        var store = storeOf(host);
        var expires = DateTimeOffset.UtcNow.Add(1.Hours());

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => store.TryClaimAsync("contended", expires, Ct)));

        results.Count(x => x).ShouldBe(1, "the _id uniqueness of the claim document makes the insert the atomic step");
    }

    // ---- MongoDB-specific ----

    [Fact]
    public async Task an_expired_claim_that_the_reaper_has_not_reached_is_claimable_again()
    {
        using var host = await buildHostAsync();
        var store = storeOf(host);
        (await store.TryClaimAsync("lapsed", DateTimeOffset.UtcNow.Subtract(1.Minutes()), Ct)).ShouldBeTrue();

        // The stored window has passed: the id protects nothing any more, so a new claim takes it over —
        // exactly once, even when two late-comers race.
        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => store.TryClaimAsync("lapsed", DateTimeOffset.UtcNow.Add(1.Hours()), Ct)));
        results.Count(x => x).ShouldBe(1);

        (await store.TryClaimAsync("lapsed", DateTimeOffset.UtcNow.Add(1.Hours()), Ct)).ShouldBeFalse("the take-over refreshed the window");
    }

    [Fact]
    public async Task release_is_idempotent()
    {
        using var host = await buildHostAsync();
        var store = storeOf(host);
        (await store.TryClaimAsync("release-me", DateTimeOffset.UtcNow.Add(1.Hours()), Ct)).ShouldBeTrue();

        await store.ReleaseAsync("release-me", Ct);
        await store.ReleaseAsync("release-me", Ct);
        await store.ReleaseAsync("never-claimed", Ct);

        (await store.TryClaimAsync("release-me", DateTimeOffset.UtcNow.Add(1.Hours()), Ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task a_rolled_back_transactional_handler_releases_its_claim_so_the_retry_runs()
    {
        using var host = await buildHostAsync();

        // Transactional chain (MongoDbUnitOfWork parameter): core emits NO compensating release frame,
        // so without TransactionalFrame's rollback release the first failure would poison the id.
        await host.TrackActivity().DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new FailingTransactionalMessage("doc-1"), new DeliveryOptions { DeduplicationId = "tx-poison-check" });
        FailingTransactionalHandler.Attempts.ShouldBe(1);
        (await Database.GetCollection<DedupDoc>(DedupDoc.Collection).CountDocumentsAsync(FilterDefinition<DedupDoc>.Empty, cancellationToken: Ct))
            .ShouldBe(0, "the handler's write rolled back with the transaction");

        // The claim is gone, so the SAME logical id reaches the handler again and this time succeeds.
        await host.SendMessageAndWaitAsync(new FailingTransactionalMessage("doc-1"), new DeliveryOptions { DeduplicationId = "tx-poison-check" });
        FailingTransactionalHandler.Attempts.ShouldBe(2);
        (await Database.GetCollection<DedupDoc>(DedupDoc.Collection).CountDocumentsAsync(FilterDefinition<DedupDoc>.Empty, cancellationToken: Ct))
            .ShouldBe(1);

        // And a third send with the same id is now a genuine duplicate: refused, handler untouched.
        await host.SendMessageAndWaitAsync(new FailingTransactionalMessage("doc-1"), new DeliveryOptions { DeduplicationId = "tx-poison-check" });
        FailingTransactionalHandler.Attempts.ShouldBe(2);
    }

    [Fact]
    public async Task the_transactional_chain_releases_the_claim_in_the_rollback_path_of_the_generated_code()
    {
        using var host = await buildHostAsync();

        var transactional = generatedSourceFor(host, typeof(FailingTransactionalMessage));
        var claim = transactional.IndexOf(".TryClaimAsync(", StringComparison.Ordinal);
        var startSession = transactional.IndexOf("StartSessionAsync(", StringComparison.Ordinal);
        var rollback = transactional.IndexOf(".RollbackAsync(", StringComparison.Ordinal);
        var release = transactional.IndexOf(".ReleaseAsync(", StringComparison.Ordinal);

        claim.ShouldBeGreaterThan(-1, "the claim frame was never woven in");
        claim.ShouldBeLessThan(startSession, "core claims BEFORE the session/transaction exists — which is why the claim is sessionless");
        release.ShouldBeGreaterThan(rollback, "the release must sit in the catch block, after the rollback");
        transactional.Contains("if (!isDuplicateMessage && !string.IsNullOrWhiteSpace(").ShouldBeTrue("the release is guarded by the claim outcome");

        // A non-transactional deduplicated chain gets core's own release frame and none of ours.
        var nonTransactional = generatedSourceFor(host, typeof(FailingDeduplicatedMessage));
        nonTransactional.ShouldNotContain("RollbackAsync(");
        nonTransactional.ShouldContain(".ReleaseAsync(");
    }

    [Fact]
    public async Task a_host_without_the_opt_in_gets_the_null_store_and_no_collection()
    {
        using var host = await buildHostAsync(enabled: false);
        await host.ResetResourceState(Ct);

        storeOf(host).ShouldBeSameAs(NullDeduplicationStore.Instance);
        storeOf(host).Enabled.ShouldBeFalse();

        var collections = await (await Database.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct);
        collections.ShouldNotContain(MongoConstants.DeduplicationCollection,
            "an upgrade must be a no-op for hosts that have not asked for the feature");
    }
}

public record DeduplicatedMessage(string Name);
public record MarkedIdentityMessage([property: DeduplicationIdentity] string InvoiceNumber, string Name);
public record ComposedIdentityMessage(string Tenant, int Sequence, string Name);
public record UnkeyedMessage(string Name);
public record FailingDeduplicatedMessage;
public record FailingTransactionalMessage(string DocId);

public class DedupDoc
{
    public const string Collection = "dedup_docs";
    public string Id { get; set; } = string.Empty;
}

[WolverineIgnore]
public static class DeduplicatedHandler
{
    public static readonly ConcurrentQueue<string> Received = new();

    [Deduplicated]
    public static void Handle(DeduplicatedMessage message) => Received.Enqueue(message.Name);
}

[WolverineIgnore]
public static class DerivedIdentityHandler
{
    public static readonly ConcurrentQueue<string> Received = new();

    [Deduplicated]
    public static void Handle(MarkedIdentityMessage message) => Received.Enqueue(message.Name);

    [Deduplicated]
    public static void Handle(ComposedIdentityMessage message) => Received.Enqueue(message.Name);
}

[WolverineIgnore]
public static class UnkeyedHandler
{
    public static readonly ConcurrentQueue<string> Received = new();

    [Deduplicated(Required = false)]
    public static void Handle(UnkeyedMessage message) => Received.Enqueue(message.Name);
}

[WolverineIgnore]
public static class FailingDeduplicatedHandler
{
    public static int Attempts;

    [Deduplicated]
    public static void Handle(FailingDeduplicatedMessage message)
    {
        Attempts++;
        throw new DivideByZeroException("nope");
    }
}

[WolverineIgnore]
public static class FailingTransactionalHandler
{
    public static int Attempts;

    // The MongoDbUnitOfWork parameter makes the chain transactional. The first attempt writes and then
    // throws, so the write rolls back; the second attempt succeeds.
    [Deduplicated]
    public static async Task Handle(FailingTransactionalMessage message, MongoDbUnitOfWork uow, CancellationToken ct)
    {
        Attempts++;
        await uow.Collection<DedupDoc>(DedupDoc.Collection).InsertOneAsync(new DedupDoc { Id = message.DocId }, ct);
        if (Attempts == 1)
        {
            throw new DivideByZeroException("first attempt fails after writing");
        }
    }
}
