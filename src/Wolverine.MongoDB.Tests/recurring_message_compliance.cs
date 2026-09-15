using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// Upstream <c>RecurringMessageCompliance</c>: publish tracking, verification/re-publish of a cancelled
/// occurrence, pause (eager cancel) / resume, manual trigger refused while paused, pause surviving a
/// restart, adoption of the predecessor's pending occurrence after a restart, unknown-schedule errors.
/// <para>
/// One inherited fact, <c>the_opt_in_is_schema_neutral_for_hosts_without_schedules</c>, casts the store to
/// <c>Weasel.Core.Migrations.IDatabase</c> to enumerate tables and therefore cannot pass for any
/// non-Weasel store; its MongoDB equivalent is <c>recurring_messages.the_opt_in_is_schema_neutral</c>.
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
}
