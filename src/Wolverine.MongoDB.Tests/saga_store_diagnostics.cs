using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.Configuration.Capabilities;
using Wolverine.MongoDB.Internals;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.MongoDB.Tests;

/// <summary>
/// Integration tests for <see cref="MongoDbSagaStoreDiagnostics"/> (T2.2), mirroring
/// <c>raven_saga_store_diagnostics_tests</c>: stands up a real host against the shared
/// Testcontainer-backed replica set so the reflective handler-graph walk, the
/// <c>FullName</c>/short-<c>Name</c> saga index, and the <c>Find</c>/<c>Limit</c> dispatch are all
/// exercised against live MongoDB rather than stubs.
/// </summary>
[Collection("mongodb")]
public class saga_store_diagnostics
{
    private readonly AppFixture _fixture;
    public saga_store_diagnostics(AppFixture fixture) => _fixture = fixture;

    private async Task<IHost> BuildHostAsync()
    {
        // Shared fixture database — clear it (incl. the per-saga-type collections) before each host
        // build so the diagnostics facts don't see instances left behind by a prior fact.
        await _fixture.ClearAll();

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Fresh compiled assembly per host (mirrors MongoDbSagaHost/saga_atomicity): avoids
                // cross-host in-memory handler reuse under a shared, message-type-keyed Auto codegen
                // name.
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

                // Only this file's sagas — never the other test handlers in the assembly.
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(DiagSaga))
                    .IncludeType(typeof(DiagGuidSaga));

                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync();
    }

    [Fact]
    public async Task registered_saga_types_includes_mongo_owned_saga()
    {
        using var host = await BuildHostAsync();

        var diagnostics = host.GetRuntime().SagaStorage;
        var registered = await diagnostics.GetRegisteredSagasAsync(CancellationToken.None);

        var diag = registered.SingleOrDefault(d => d.StateType.FullName == typeof(DiagSaga).FullName);
        diag.ShouldNotBeNull();
        diag.StorageProvider.ShouldBe("MongoDb");
        diag.Messages
            .Where(m => m.Role == SagaRole.Start || m.Role == SagaRole.StartOrHandle)
            .Select(m => m.MessageType.FullName)
            .ShouldContain(typeof(StartDiagSaga).FullName!);
    }

    [Fact]
    public async Task read_saga_returns_state_for_existing_instance_by_full_name()
    {
        using var host = await BuildHostAsync();
        var sagaId = "diag/" + Guid.NewGuid().ToString("N");
        await host.InvokeMessageAndWaitAsync(new StartDiagSaga(sagaId, "alpha"));

        var diagnostics = host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(typeof(DiagSaga).FullName!, sagaId, CancellationToken.None);

        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeFalse();
        state.State.GetProperty("Note").GetString().ShouldBe("alpha");
    }

    [Fact]
    public async Task read_saga_returns_state_for_existing_instance_by_short_name()
    {
        using var host = await BuildHostAsync();
        var sagaId = "diag/" + Guid.NewGuid().ToString("N");
        await host.InvokeMessageAndWaitAsync(new StartDiagSaga(sagaId, "beta"));

        var diagnostics = host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(nameof(DiagSaga), sagaId, CancellationToken.None);

        state.ShouldNotBeNull();
        state.IsCompleted.ShouldBeFalse();
        state.State.GetProperty("Note").GetString().ShouldBe("beta");
    }

    [Fact]
    public async Task read_saga_returns_null_for_missing_instance()
    {
        using var host = await BuildHostAsync();

        var diagnostics = host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(
            typeof(DiagSaga).FullName!, "diag/missing-" + Guid.NewGuid(), CancellationToken.None);

        state.ShouldBeNull();
    }

    [Fact]
    public async Task list_saga_instances_returns_recent_sagas()
    {
        using var host = await BuildHostAsync();
        await host.InvokeMessageAndWaitAsync(new StartDiagSaga("diag/" + Guid.NewGuid().ToString("N"), "one"));
        await host.InvokeMessageAndWaitAsync(new StartDiagSaga("diag/" + Guid.NewGuid().ToString("N"), "two"));

        var diagnostics = host.GetRuntime().SagaStorage;
        var instances = await diagnostics.ListSagaInstancesAsync(typeof(DiagSaga).FullName!, 10, CancellationToken.None);

        instances.Count.ShouldBeGreaterThanOrEqualTo(2);
        instances.ShouldAllBe(i => i.SagaTypeName == typeof(DiagSaga).FullName);
    }

    [Fact]
    public async Task unknown_saga_type_returns_null_and_empty()
    {
        using var host = await BuildHostAsync();

        var diagnostics = host.GetRuntime().SagaStorage;
        var read = await diagnostics.ReadSagaAsync("Some.Unknown.Saga", "anything", CancellationToken.None);
        var list = await diagnostics.ListSagaInstancesAsync("Some.Unknown.Saga", 10, CancellationToken.None);

        read.ShouldBeNull();
        list.ShouldBeEmpty();
    }

    [Fact]
    public async Task read_saga_returns_state_for_guid_keyed_instance_by_guid_identity()
    {
        using var host = await BuildHostAsync();
        var sagaId = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartDiagGuidSaga(sagaId, "gamma"));

        var diagnostics = host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(typeof(DiagGuidSaga).FullName!, sagaId, CancellationToken.None);

        state.ShouldNotBeNull();
        state.State.GetProperty("Note").GetString().ShouldBe("gamma");
    }

    [Fact]
    public async Task read_saga_returns_state_for_guid_keyed_instance_by_string_identity()
    {
        using var host = await BuildHostAsync();
        var sagaId = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new StartDiagGuidSaga(sagaId, "delta"));

        var diagnostics = host.GetRuntime().SagaStorage;
        var state = await diagnostics.ReadSagaAsync(typeof(DiagGuidSaga).FullName!, sagaId.ToString(), CancellationToken.None);

        state.ShouldNotBeNull();
        state.State.GetProperty("Note").GetString().ShouldBe("delta");
    }
}

public record StartDiagSaga(string Id, string Note);

public class DiagSaga : Wolverine.Saga
{
    public string Id { get; set; } = "";
    public string Note { get; set; } = "";

    public void Start(StartDiagSaga cmd)
    {
        Id = cmd.Id;
        Note = cmd.Note;
    }
}

public record StartDiagGuidSaga(Guid Id, string Note);

public class DiagGuidSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public string Note { get; set; } = "";

    public void Start(StartDiagGuidSaga cmd)
    {
        Id = cmd.Id;
        Note = cmd.Note;
    }
}
