using Microsoft.Extensions.DependencyInjection;
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
/// One inherited fact, <c>the_opt_in_is_schema_neutral_for_hosts_without_schedules</c>, casts the store to
/// <c>Weasel.Core.Migrations.IDatabase</c> to enumerate tables, which no non-Weasel store can satisfy
/// (upstream only the four RDBMS providers inherit this suite). It is hidden here by a <c>new</c> fact of
/// the same name that asserts the same contract against MongoDB collections: a schedule-less host
/// migrates without provisioning the recurring or deduplication collection and exposes the null store,
/// while a single <c>ScheduleRecurring</c> registration opts both in.
/// </para>
/// </summary>
[Collection("mongodb")]
public class recurring_message_compliance : RecurringMessageCompliance
{
    private readonly AppFixture _fixture;
    public recurring_message_compliance(AppFixture fixture) => _fixture = fixture;

    protected override void configurePersistence(WolverineOptions opts)
    {
        opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
        opts.UseMongoDbPersistence(AppFixture.DatabaseName);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    private async Task<string[]> collectionNamesAsync()
        => (await (await Database.ListCollectionNamesAsync(cancellationToken: Ct)).ToListAsync(Ct)).ToArray();

    /// <summary>
    /// MongoDB replacement for the upstream fact of the same name (see the class summary). Weasel's
    /// <c>AllObjects()</c> lists what a store <em>declares</em>; the MongoDB analogue is what
    /// <c>MigrateAsync</c> provisions, so the collections are dropped first and the assertion is that a
    /// schedule-less host's startup migration leaves them absent.
    /// </summary>
    // xUnit1024 ("test methods cannot have overloads") also fires for a same-signature `new` method, but this
    // is hide-by-signature, not an overload: xUnit v3 discovers exactly one method of this name on this class
    // (verified with --list-tests), so the Weasel-bound base fact never runs here. Scoped to this method only.
#pragma warning disable xUnit1024
    [Fact]
    public new async Task the_opt_in_is_schema_neutral_for_hosts_without_schedules()
#pragma warning restore xUnit1024
    {
        await _fixture.ClearAll();
        await Database.DropCollectionAsync(MongoConstants.RecurringMessagesCollection, Ct);
        await Database.DropCollectionAsync(MongoConstants.DeduplicationCollection, Ct);

        // clean: false — the pre-flight host forces both durability flags on and would provision
        // exactly the collections whose absence is under test.
        var without = await buildHost(_ => { }, clean: false);

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
        var with = await buildHost(opts =>
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
    }
}
