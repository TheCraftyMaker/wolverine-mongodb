using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests.ExclusiveListeners;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// Upstream <c>ExclusiveListenerRecoveryCompliance</c> (GH-3590): durable listeners that are only
/// ever active on one node (<c>ListenerScope.Exclusive</c> / <c>PinnedToLeader</c>) recover their own
/// dormant inbox rows through <c>ListenerInboxRecovery</c>, and the durability agent must leave those
/// rows alone. The recovery works purely against <c>IMessageStore.LoadPageOfGloballyOwnedIncomingAsync</c>
/// and <c>ReassignIncomingAsync</c>, so the only thing this store supplies is its bootstrapping.
/// </summary>
[Collection("mongodb")]
public class exclusive_listener_recovery_compliance : ExclusiveListenerRecoveryCompliance
{
    private readonly AppFixture _fixture;
    public exclusive_listener_recovery_compliance(AppFixture fixture) => _fixture = fixture;

    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.Services.AddSingleton<IMongoClient>(_fixture.Client);
        options.UseMongoDbPersistence(AppFixture.DatabaseName);
    }

    public override async ValueTask InitializeAsync()
    {
        await _fixture.ClearAll();
        await base.InitializeAsync();
    }
}
