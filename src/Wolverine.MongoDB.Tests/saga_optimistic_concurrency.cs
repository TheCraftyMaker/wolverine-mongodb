using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Shouldly;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.MongoDB.Internals;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.MongoDB.Tests;

/// <summary>
/// Optimistic-concurrency oracle for S8. The upstream saga compliance suite does not exercise
/// concurrency, so the OCC contract is proven here: <see cref="Saga.Version"/> is stamped on
/// insert and incremented on every update through the generated frame, and a stale-version update
/// throws <see cref="SagaConcurrencyException"/> without clobbering the winning write.
/// </summary>
[Collection("mongodb")]
public class saga_optimistic_concurrency
{
    [Fact]
    public async Task version_starts_at_one_and_increments_through_the_generated_update_frame()
    {
        var sagaHost = new MongoDbSagaHost();
        using var host = await sagaHost.BuildHostAsync<GuidBasicWorkflow>();

        var id = Guid.NewGuid();

        // The insert frame stamps the initial version.
        await host.InvokeMessageAndWaitAsync(new GuidStart { Id = id, Name = "Croaker" });

        var afterStart = await sagaHost.LoadState<GuidBasicWorkflow>(id);
        afterStart.ShouldNotBeNull();
        afterStart.Version.ShouldBe(1);

        // A single update through the real generated update frame advances the version by exactly one
        // and does not spuriously trip the concurrency guard on the happy path.
        await host.InvokeMessageAndWaitAsync(new GuidCompleteThree { SagaId = id });

        var afterUpdate = await sagaHost.LoadState<GuidBasicWorkflow>(id);
        afterUpdate.ShouldNotBeNull();
        afterUpdate.ThreeCompleted.ShouldBeTrue();
        afterUpdate.Version.ShouldBe(2);
    }

    [Fact]
    public async Task stale_version_update_throws_saga_concurrency_exception_and_does_not_clobber()
    {
        var sagaHost = new MongoDbSagaHost();
        using var host = await sagaHost.BuildHostAsync<GuidBasicWorkflow>();

        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new GuidStart { Id = id, Name = "original" });

        var client = host.Services.GetRequiredService<IMongoClient>();
        var database = client.GetDatabase(AppFixture.DatabaseName);
        using var session = await client.StartSessionAsync();

        // Two stores load the same saga at the same (initial) version — the classic OCC race.
        var winner = await MongoSagaOperations.LoadSagaAsync<GuidBasicWorkflow, Guid>(database, session, id, default);
        var loser = await MongoSagaOperations.LoadSagaAsync<GuidBasicWorkflow, Guid>(database, session, id, default);
        winner.ShouldNotBeNull();
        loser.ShouldNotBeNull();
        winner.Version.ShouldBe(1);
        loser.Version.ShouldBe(1);

        // The winner updates first: succeeds, version 1 -> 2.
        winner.Name = "winner";
        await MongoSagaOperations.UpdateSagaAsync<GuidBasicWorkflow, Guid>(database, session, winner, id, default);
        winner.Version.ShouldBe(2);

        // The loser is still at the stale version 1, so its guarded update matches no document and
        // surfaces the optimistic-concurrency violation rather than overwriting the winner.
        loser.Name = "loser";
        await Should.ThrowAsync<SagaConcurrencyException>(async () =>
            await MongoSagaOperations.UpdateSagaAsync<GuidBasicWorkflow, Guid>(database, session, loser, id, default));

        // No clobber: the persisted document reflects the winner's write, at version 2.
        var persisted = await sagaHost.LoadState<GuidBasicWorkflow>(id);
        persisted.ShouldNotBeNull();
        persisted.Name.ShouldBe("winner");
        persisted.Version.ShouldBe(2);
    }
}
