using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// Upstream <c>DeduplicationStoreCompliance</c>, under both <see cref="MessageDeduplicationMode"/>s.
/// <para>
/// Hosted by composition, the way <c>recurring_message_compliance</c> hosts its suite: the private
/// <see cref="Bridge"/> derives from the upstream suite and each upstream fact is an owned, same-named fact here.
/// Nineteen forward to the suite. Twelve cannot, because upstream answers them through SQL (table and column
/// introspection, <c>alter table</c>, a <c>DbConnection</c> transaction) and a Mongo collection has none of
/// those. Each of the twelve is replaced by a same-named fact that asserts the same contract against the
/// collection. <see cref="every_upstream_fact_is_owned_here"/> fails loudly if upstream adds a fact this class
/// does not own.
/// </para>
/// <para>
/// Both modes map to one store. The modes exist so an RDBMS can stop its collation deciding which ids are the same
/// claim, and the claim key here is the string <c>_id</c>, which a collection with no collation compares byte
/// for byte.
/// </para>
/// </summary>
[Collection("mongodb")]
public class deduplication_store_compliance : IAsyncLifetime
{
    private readonly AppFixture _fixture;
    private readonly List<IHost> _hosts = [];
    private readonly Bridge _bridge;

    public deduplication_store_compliance(AppFixture fixture)
    {
        _fixture = fixture;
        _bridge = new Bridge(fixture);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    private IMongoCollection<BsonDocument> Claims
        => Database.GetCollection<BsonDocument>(MongoConstants.DeduplicationCollection);

    private static DateTimeOffset anHourFromNow => DateTimeOffset.UtcNow.AddHours(1);

    private sealed class Bridge : DeduplicationStoreCompliance
    {
        private readonly AppFixture _fixture;
        public Bridge(AppFixture fixture) => _fixture = fixture;

        protected override void configurePersistence(WolverineOptions opts)
        {
            opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
            opts.UseMongoDbPersistence(AppFixture.DatabaseName);
        }

        // Never read: the only facts that use them are the replaced ones.
        protected override string deduplicationTableName => MongoConstants.DeduplicationCollection;
        protected override string hashedDeduplicationTableName => MongoConstants.DeduplicationCollection;

        // Collection ids compare byte for byte under both modes.
        protected override bool stringComparisonIsCaseSensitive => true;
    }

    public ValueTask InitializeAsync() => _bridge.InitializeAsync();

    public async ValueTask DisposeAsync()
    {
        await _bridge.DisposeAsync();
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync();
            }
            finally
            {
                host.Dispose();
            }
        }
    }

    private async Task<IHost> startHostAsync(MessageDeduplicationMode mode, bool rebuild = true)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageDeduplicationMode = mode;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(Ct);

        _hosts.Add(host);
        if (rebuild) await host.RebuildAllEnvelopeStorageAsync();
        return host;
    }

    private static IDeduplicationStore storeFor(IHost host) => host.GetRuntime().Storage.Deduplication;

    private static IEnvelopeAwareDeduplicationStore envelopeAwareStoreFor(IHost host)
        => storeFor(host).ShouldBeAssignableTo<IEnvelopeAwareDeduplicationStore>()!;

    private async Task<string[]> storedIdsAsync()
        => (await Claims.Find(new BsonDocument()).ToListAsync(Ct)).Select(x => x["_id"].AsString).ToArray();

    private Task seedClaimWithoutAnEnvelopeAsync(string id)
        => Claims.InsertOneAsync(new BsonDocument { { "_id", id }, { "expires", anHourFromNow.UtcDateTime } },
            cancellationToken: Ct);

    // ------------------------------------------------------------------------------------------------
    // Forwarded upstream facts
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public Task ids_differing_only_in_case_are_distinct_claims_under_hash_comparison()
        => _bridge.ids_differing_only_in_case_are_distinct_claims_under_hash_comparison();

    [Fact]
    public Task ids_differing_only_in_accent_are_distinct_claims_under_hash_comparison()
        => _bridge.ids_differing_only_in_accent_are_distinct_claims_under_hash_comparison();

    [Fact]
    public Task an_exact_repeat_is_still_refused_under_hash_comparison()
        => _bridge.an_exact_repeat_is_still_refused_under_hash_comparison();

    [Fact]
    public Task releasing_a_claim_lets_the_same_id_be_claimed_again_under_hash_comparison()
        => _bridge.releasing_a_claim_lets_the_same_id_be_claimed_again_under_hash_comparison();

    [Fact]
    public Task releasing_one_id_does_not_release_a_case_variant_of_it_under_hash_comparison()
        => _bridge.releasing_one_id_does_not_release_a_case_variant_of_it_under_hash_comparison();

    [Fact]
    public Task the_reaper_deletes_expired_claims_under_hash_comparison()
        => _bridge.the_reaper_deletes_expired_claims_under_hash_comparison();

    [Fact]
    public Task concurrent_claims_of_one_id_produce_exactly_one_winner_under_hash_comparison()
        => _bridge.concurrent_claims_of_one_id_produce_exactly_one_winner_under_hash_comparison();

    [Fact]
    public Task string_comparison_keeps_this_engines_collation_behaviour()
        => _bridge.string_comparison_keeps_this_engines_collation_behaviour();

    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public Task a_claim_can_be_looked_up_without_taking_it(MessageDeduplicationMode mode)
        => _bridge.a_claim_can_be_looked_up_without_taking_it(mode);

    [Fact]
    public Task string_comparison_still_refuses_an_exact_repeat()
        => _bridge.string_comparison_still_refuses_an_exact_repeat();

    [Fact]
    public Task string_comparison_still_releases_and_reaps()
        => _bridge.string_comparison_still_releases_and_reaps();

    [Fact]
    public Task concurrent_claims_of_one_id_produce_exactly_one_winner_under_string_comparison()
        => _bridge.concurrent_claims_of_one_id_produce_exactly_one_winner_under_string_comparison();

    [Fact]
    public Task none_leaves_the_null_store_in_place() => _bridge.none_leaves_the_null_store_in_place();

    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public Task the_same_envelope_takes_over_a_claim_it_left_behind(MessageDeduplicationMode mode)
        => _bridge.the_same_envelope_takes_over_a_claim_it_left_behind(mode);

    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public Task a_different_envelope_is_still_refused(MessageDeduplicationMode mode)
        => _bridge.a_different_envelope_is_still_refused(mode);

    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public Task a_claim_recorded_without_an_envelope_is_never_taken_over(MessageDeduplicationMode mode)
        => _bridge.a_claim_recorded_without_an_envelope_is_never_taken_over(mode);

    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public Task taking_a_claim_over_renews_its_expiry(MessageDeduplicationMode mode)
        => _bridge.taking_a_claim_over_renews_its_expiry(mode);

    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public Task releasing_an_envelope_claim_lets_another_envelope_claim_the_id(MessageDeduplicationMode mode)
        => _bridge.releasing_an_envelope_claim_lets_another_envelope_claim_the_id(mode);

    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public Task concurrent_envelope_claims_of_one_id_produce_exactly_one_winner(MessageDeduplicationMode mode)
        => _bridge.concurrent_envelope_claims_of_one_id_produce_exactly_one_winner(mode);

    // ------------------------------------------------------------------------------------------------
    // MongoDB replacements for the facts upstream answers through SQL
    // ------------------------------------------------------------------------------------------------

    /// <summary>The readable id is the document's own <c>_id</c>, so an operator sees which claim is stuck.</summary>
    [Fact]
    public async Task the_readable_id_is_stored_alongside_the_hash()
    {
        var host = await startHostAsync(MessageDeduplicationMode.CompareByHash);

        await storeFor(host).TryClaimAsync("Invoice-17|José", anHourFromNow, Ct);

        (await storedIdsAsync()).ShouldBe(["Invoice-17|José"]);
    }

    /// <summary>Hash mode provisions the one claim collection and no second, hashed one.</summary>
    [Fact]
    public async Task hash_comparison_provisions_only_the_hashed_table()
    {
        await startHostAsync(MessageDeduplicationMode.CompareByHash);

        var names = await (await Database.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct);

        names.Where(x => x.StartsWith(MongoConstants.DeduplicationCollection, StringComparison.Ordinal))
            .ShouldBe([MongoConstants.DeduplicationCollection]);
    }

    /// <summary>
    /// The key is a BSON string in a collection with no collation, so ids compare byte for byte: the property the
    /// binary hash column buys on an RDBMS.
    /// </summary>
    [Fact]
    public async Task the_hash_column_is_binary()
    {
        var host = await startHostAsync(MessageDeduplicationMode.CompareByHash);
        await storeFor(host).TryClaimAsync("Abc", anHourFromNow, Ct);

        (await Claims.Find(new BsonDocument()).SingleAsync(Ct))["_id"].BsonType.ShouldBe(BsonType.String);

        var listed = await (await Database.ListCollectionsAsync(
            new ListCollectionsOptions { Filter = new BsonDocument("name", MongoConstants.DeduplicationCollection) },
            Ct)).SingleAsync(Ct);
        listed["options"].AsBsonDocument.Contains("collation").ShouldBeFalse();
    }

    /// <summary>A claim document holds the id, the expiry and the envelope, and nothing else.</summary>
    [Fact]
    public async Task string_comparison_provisions_only_the_original_shape_plus_the_envelope_column()
    {
        var host = await startHostAsync(MessageDeduplicationMode.CompareByString);
        await envelopeAwareStoreFor(host).TryClaimAsync("shape", Guid.NewGuid(), anHourFromNow, Ct);

        (await Claims.Find(new BsonDocument()).SingleAsync(Ct)).Names
            .ShouldBe(["_id", "expires", "envelopeId"], ignoreOrder: true);
    }

    /// <summary>One collection serves both modes, so a switch leaves every live claim where it was.</summary>
    [Fact]
    public async Task switching_to_hash_comparison_leaves_the_original_tables_rows_untouched()
    {
        var before = await startHostAsync(MessageDeduplicationMode.CompareByString);
        await storeFor(before).TryClaimAsync("pre-switch-1", anHourFromNow, Ct);
        await storeFor(before).TryClaimAsync("pre-switch-2", anHourFromNow, Ct);
        await before.StopAsync(Ct);

        var after = await startHostAsync(MessageDeduplicationMode.CompareByHash, rebuild: false);
        await after.GetRuntime().Storage.Admin.MigrateAsync();

        (await storedIdsAsync()).ShouldBe(["pre-switch-1", "pre-switch-2"], ignoreOrder: true);

        var hashed = storeFor(after);
        (await hashed.TryClaimAsync("post-switch", anHourFromNow, Ct)).ShouldBeTrue();
        (await hashed.TryClaimAsync("post-switch", anHourFromNow, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task switching_back_to_string_comparison_leaves_the_hashed_tables_rows_untouched()
    {
        var before = await startHostAsync(MessageDeduplicationMode.CompareByHash);
        await storeFor(before).TryClaimAsync("hashed-claim", anHourFromNow, Ct);
        await before.StopAsync(Ct);

        var after = await startHostAsync(MessageDeduplicationMode.CompareByString, rebuild: false);
        await after.GetRuntime().Storage.Admin.MigrateAsync();

        (await storedIdsAsync()).ShouldBe(["hashed-claim"]);

        var strings = storeFor(after);
        (await strings.TryClaimAsync("string-claim", anHourFromNow, Ct)).ShouldBeTrue();
        (await strings.TryClaimAsync("string-claim", anHourFromNow, Ct)).ShouldBeFalse();
    }

    /// <summary>
    /// The claim is sessionless, written before the handler's transaction opens. Upstream's claim rides the
    /// caller's transaction and a rollback takes it back. Here a rollback leaves the claim in place, and
    /// <c>TransactionalFrame</c> releases it explicitly, which the transaction facts in
    /// <c>logical_message_deduplication</c> cover.
    /// </summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task a_claim_in_a_rolled_back_transaction_was_never_made(MessageDeduplicationMode mode)
    {
        var host = await startHostAsync(mode);

        (await claimBesideATransactionAsync(host, "rolled-back", commit: false)).ShouldBeTrue();

        (await storeFor(host).TryClaimAsync("rolled-back", anHourFromNow, Ct))
            .ShouldBeFalse("a sessionless claim is not part of the transaction that rolled back");
    }

    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task a_claim_in_a_committed_transaction_refuses_the_next_one(MessageDeduplicationMode mode)
    {
        var host = await startHostAsync(mode);

        (await claimBesideATransactionAsync(host, "committed", commit: true)).ShouldBeTrue();

        (await storeFor(host).TryClaimAsync("committed", anHourFromNow, Ct)).ShouldBeFalse();
    }

    /// <summary>A refused duplicate raises no write error, so it cannot abort an open transaction.</summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task an_already_claimed_id_is_refused_in_a_transaction_that_can_still_commit(
        MessageDeduplicationMode mode)
    {
        var host = await startHostAsync(mode);
        (await storeFor(host).TryClaimAsync("taken", anHourFromNow, Ct)).ShouldBeTrue();

        (await claimBesideATransactionAsync(host, "taken", commit: true)).ShouldBeFalse();
    }

    /// <summary>
    /// Opens a transaction, makes one enlisted write, claims <paramref name="id"/>, then ends the transaction.
    /// The commit must succeed whether or not the claim was refused, and the enlisted write must follow the
    /// transaction's outcome.
    /// </summary>
    private async Task<bool> claimBesideATransactionAsync(IHost host, string id, bool commit)
    {
        var scratch = Database.GetCollection<BsonDocument>("dedup_compliance_scratch_" + Guid.NewGuid().ToString("N"));
        try
        {
            using var session = await _fixture.Client.StartSessionAsync(cancellationToken: Ct);
            session.StartTransaction();
            await scratch.InsertOneAsync(session, new BsonDocument("_id", id), cancellationToken: Ct);

            var claimed = await storeFor(host).TryClaimAsync(id, anHourFromNow, Ct);

            if (commit) await session.CommitTransactionAsync(Ct);
            else await session.AbortTransactionAsync(Ct);

            (await scratch.CountDocumentsAsync(new BsonDocument(), cancellationToken: Ct))
                .ShouldBe(commit ? 1 : 0);
            return claimed;
        }
        finally
        {
            await Database.DropCollectionAsync(scratch.CollectionNamespace.CollectionName, Ct);
        }
    }

    /// <summary>
    /// A claim written before envelopes were recorded has no <c>envelopeId</c>. A collection takes that document
    /// as it is, so the migration is a no-op, the claim survives it, and it still refuses every envelope.
    /// </summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task an_existing_table_without_the_envelope_column_migrates_and_keeps_its_claims(
        MessageDeduplicationMode mode)
    {
        var before = await startHostAsync(mode);
        await seedClaimWithoutAnEnvelopeAsync("pre-upgrade");
        await before.StopAsync(Ct);

        var after = await startHostAsync(mode, rebuild: false);
        await after.GetRuntime().Storage.Admin.MigrateAsync();

        (await storedIdsAsync()).ShouldBe(["pre-upgrade"]);
        (await envelopeAwareStoreFor(after).TryClaimAsync("pre-upgrade", Guid.NewGuid(), anHourFromNow, Ct))
            .ShouldBeFalse();
    }

    /// <summary>A claim with no recorded envelope keeps the original semantics, with or without a migration.</summary>
    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task an_unmigrated_table_keeps_the_original_claim_semantics(MessageDeduplicationMode mode)
    {
        var host = await startHostAsync(mode);
        await seedClaimWithoutAnEnvelopeAsync("unmigrated");

        var store = envelopeAwareStoreFor(host);
        var envelopeId = Guid.NewGuid();

        (await store.TryClaimAsync("unmigrated", envelopeId, anHourFromNow, Ct)).ShouldBeFalse();
        (await store.TryClaimAsync("unmigrated", Guid.NewGuid(), anHourFromNow, Ct)).ShouldBeFalse();
        (await storeFor(host).TryClaimAsync("unmigrated", anHourFromNow, Ct)).ShouldBeFalse();

        await storeFor(host).ReleaseAsync("unmigrated", Ct);
        (await store.TryClaimAsync("unmigrated", Guid.NewGuid(), anHourFromNow, Ct)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(MessageDeduplicationMode.CompareByHash)]
    [InlineData(MessageDeduplicationMode.CompareByString)]
    public async Task after_the_migration_and_a_restart_claims_record_the_envelope(MessageDeduplicationMode mode)
    {
        var before = await startHostAsync(mode);
        await seedClaimWithoutAnEnvelopeAsync("pre-migration");
        await before.StopAsync(Ct);

        var after = await startHostAsync(mode, rebuild: false);
        await after.GetRuntime().Storage.Admin.MigrateAsync();

        var store = envelopeAwareStoreFor(after);
        var envelopeId = Guid.NewGuid();

        (await store.TryClaimAsync("post-migration", envelopeId, anHourFromNow, Ct)).ShouldBeTrue();
        (await store.TryClaimAsync("post-migration", envelopeId, anHourFromNow, Ct))
            .ShouldBeTrue("the claim records the envelope, so the same envelope takes its claim over");

        (await store.TryClaimAsync("pre-migration", Guid.NewGuid(), anHourFromNow, Ct)).ShouldBeFalse();
    }

    /// <summary>
    /// Drift guard for the composition: every fact the upstream suite declares must exist as a same-named fact on
    /// this class, forwarded or replaced.
    /// </summary>
    [Fact]
    public void every_upstream_fact_is_owned_here()
    {
        static IEnumerable<string> facts(Type type) => type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
            .Select(m => m.Name);

        var missing = facts(typeof(DeduplicationStoreCompliance)).Except(facts(GetType())).ToArray();
        missing.ShouldBeEmpty($"Upstream DeduplicationStoreCompliance facts not owned here: {string.Join(", ", missing)}");
    }
}
