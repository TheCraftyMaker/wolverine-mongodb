using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// Upstream <c>RecurringMessageCompliance</c>: publish tracking, verification/re-publish of a cancelled
/// occurrence, pause (eager cancel) / resume, manual trigger refused while paused, pause surviving a
/// restart, adoption of the predecessor's pending occurrence after a restart, unknown-schedule errors.
/// <para>
/// Hosted by composition rather than inheritance, the way <c>RavenDbFaultPublishingTests</c> hosts
/// <c>DurableFaultPublishingCompliance</c> upstream: the private <see cref="Bridge"/> derives from the
/// suite and every upstream fact is an owned, same-named fact here that forwards to it. That lets the
/// one fact no non-Weasel store can satisfy — <c>the_opt_in_is_schema_neutral_for_hosts_without_schedules</c>
/// casts the store to <c>Weasel.Core.Migrations.IDatabase</c> to enumerate tables, and upstream only the four
/// RDBMS providers inherit this suite — be replaced by a MongoDB-native fact of the same name asserting the
/// same contract against collections. <see cref="every_upstream_fact_is_owned_here"/> fails loudly if
/// upstream adds a fact this class does not forward.
/// </para>
/// </summary>
[Collection("mongodb")]
public class recurring_message_compliance
{
    private readonly AppFixture _fixture;
    public recurring_message_compliance(AppFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    private sealed class Bridge : RecurringMessageCompliance
    {
        private readonly AppFixture _fixture;
        public Bridge(AppFixture fixture) => _fixture = fixture;

        protected override void configurePersistence(WolverineOptions opts)
        {
            opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
            opts.UseMongoDbPersistence(AppFixture.DatabaseName);
        }

        /// <summary>Exposes the suite's tracked host builder to the owning class's MongoDB-native fact.</summary>
        public Task<IHost> BuildHostAsync(Action<WolverineOptions> configure, bool clean)
            => buildHost(configure, clean);
    }

    /// <summary>
    /// Runs one upstream fact with the suite's own <c>IAsyncLifetime</c> around it (handler-static reset
    /// before, host stop/dispose after) — xUnit would do this for an inherited fact; the bridge is not a
    /// test class, so this class does it.
    /// </summary>
    private static async Task run(Bridge bridge, Func<Bridge, Task> fact)
    {
        await bridge.InitializeAsync();
        try
        {
            await fact(bridge);
        }
        finally
        {
            await bridge.DisposeAsync();
        }
    }

    private Task run(Func<Bridge, Task> fact) => run(new Bridge(_fixture), fact);

    [Fact]
    public Task publishing_upserts_the_tracking_row_and_the_envelope_is_really_scheduled()
        => run(b => b.publishing_upserts_the_tracking_row_and_the_envelope_is_really_scheduled());

    [Fact]
    public Task publishing_records_the_tracking_row_for_a_non_utc_schedule()
        => run(b => b.publishing_records_the_tracking_row_for_a_non_utc_schedule());

    [Fact]
    public Task verification_detects_a_cancelled_envelope_and_republishes_it()
        => run(b => b.verification_detects_a_cancelled_envelope_and_republishes_it());

    [Fact]
    public Task pause_marks_the_row_and_eagerly_cancels_the_pending_envelope()
        => run(b => b.pause_marks_the_row_and_eagerly_cancels_the_pending_envelope());

    [Fact]
    public Task a_manual_trigger_runs_once_leaves_the_cadence_alone_and_is_refused_while_paused()
        => run(b => b.a_manual_trigger_runs_once_leaves_the_cadence_alone_and_is_refused_while_paused());

    [Fact]
    public Task pause_survives_restart_nothing_fires_while_paused_and_resume_is_strictly_after_now()
        => run(b => b.pause_survives_restart_nothing_fires_while_paused_and_resume_is_strictly_after_now());

    [Fact]
    public Task a_restarted_host_adopts_the_predecessors_pending_occurrence_and_it_still_fires()
        => run(b => b.a_restarted_host_adopts_the_predecessors_pending_occurrence_and_it_still_fires());

    [Fact]
    public Task pausing_an_unknown_schedule_throws()
        => run(b => b.pausing_an_unknown_schedule_throws());

    /// <summary>
    /// MongoDB replacement for the upstream fact of the same name (see the class summary). Weasel's
    /// <c>AllObjects()</c> lists what a store <em>declares</em>; the MongoDB analogue is what
    /// <c>MigrateAsync</c> provisions, so the collections are dropped first and the assertion is that a
    /// schedule-less host's migration leaves them absent while one schedule registration provisions them.
    /// </summary>
    [Fact]
    public Task the_opt_in_is_schema_neutral_for_hosts_without_schedules() => run(async bridge =>
    {
        await _fixture.ClearAll();
        await Database.DropCollectionAsync(MongoConstants.RecurringMessagesCollection, Ct);
        await Database.DropCollectionAsync(MongoConstants.DeduplicationCollection, Ct);

        // clean: false — the suite's pre-flight host forces both durability flags on and would provision
        // exactly the collections whose absence is under test.
        var without = await bridge.BuildHostAsync(_ => { }, clean: false);

        var store = without.Services.GetRequiredService<IMessageStore>();
        store.RecurringMessages.Enabled.ShouldBeFalse();
        store.RecurringMessages.ShouldBeSameAs(NullRecurringMessageStore.Instance);
        store.Deduplication.ShouldBeSameAs(NullDeduplicationStore.Instance);

        await store.Admin.MigrateAsync();
        var names = await collectionNamesAsync();
        names.ShouldNotContain(MongoConstants.RecurringMessagesCollection);
        names.ShouldNotContain(MongoConstants.DeduplicationCollection);

        await without.StopAsync(Ct);

        // And the registration IS the whole opt-in: one schedule provisions both.
        var with = await bridge.BuildHostAsync(opts =>
        {
            opts.Schedules.ScheduleRecurring<RecurringComplianceMessage>("neutrality-compliance", "0 * * * *",
                _ => new RecurringComplianceMessage());
        }, clean: false);

        var optedIn = with.Services.GetRequiredService<IMessageStore>();
        optedIn.RecurringMessages.Enabled.ShouldBeTrue();
        optedIn.RecurringMessages.ShouldBeOfType<MongoDbRecurringMessageStore>();
        optedIn.Deduplication.ShouldBeOfType<MongoDbDeduplicationStore>();

        await optedIn.Admin.MigrateAsync();
        (await collectionNamesAsync()).ShouldContain(MongoConstants.DeduplicationCollection);
    });

    /// <summary>
    /// Drift guard for the composition: every <c>[Fact]</c> the upstream suite declares must exist as a
    /// same-named <c>[Fact]</c> on this class (forwarded or replaced), so a new upstream fact is never
    /// silently dropped.
    /// </summary>
    [Fact]
    public void every_upstream_fact_is_owned_here()
    {
        static IEnumerable<string> facts(Type type) => type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
            .Select(m => m.Name);

        var missing = facts(typeof(RecurringMessageCompliance)).Except(facts(GetType())).ToArray();
        missing.ShouldBeEmpty($"Upstream RecurringMessageCompliance facts not forwarded here: {string.Join(", ", missing)}");
    }

    private async Task<string[]> collectionNamesAsync()
        => (await (await Database.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct)).ToArray();
}
