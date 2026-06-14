using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;

namespace Wolverine.MongoDB.Tests;

public class FrameTestDoc
{
    public string Id { get; set; } = string.Empty;
}

public record CollectionOnlyCommand(Guid Id);
public record SessionOnlyCommand(Guid Id);

public static class CollectionOnlyHandler
{
    // Depends on IMongoCollection<T> (registered in DI below), NOT IMongoDatabase.
    // Must still receive the transactional frame + session.
    public static Task Handle(CollectionOnlyCommand cmd, IMongoCollection<FrameTestDoc> docs,
        IClientSessionHandle session, CancellationToken ct)
        => docs.InsertOneAsync(session, new FrameTestDoc { Id = cmd.Id.ToString() }, cancellationToken: ct);
}

public static class SessionOnlyHandler
{
    // Declares ONLY the session. Without the frame this fails codegen
    // (no variable can supply IClientSessionHandle).
    public static Task Handle(SessionOnlyCommand cmd, IClientSessionHandle session)
        => Task.CompletedTask;
}

[Collection("mongodb")]
public class transaction_frame_application
{
    private readonly AppFixture _fixture;
    public transaction_frame_application(AppFixture fixture) => _fixture = fixture;

    private async Task<IHost> BuildHost()
    {
        await _fixture.ClearAll();
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Services.AddSingleton(sp =>
                    sp.GetRequiredService<IMongoDatabase>().GetCollection<FrameTestDoc>("frame_test_docs"));
                opts.Policies.AutoApplyTransactions();
                opts.Discovery.IncludeType(typeof(CollectionOnlyHandler));
                opts.Discovery.IncludeType(typeof(SessionOnlyHandler));
            }).StartAsync();
    }

    [Fact]
    public async Task handler_with_collection_dependency_gets_a_transaction_and_writes()
    {
        using var host = await BuildHost();
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var id = Guid.NewGuid();

        await bus.InvokeAsync(new CollectionOnlyCommand(id));

        var collection = _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<FrameTestDoc>("frame_test_docs");
        var doc = await collection.Find(x => x.Id == id.ToString()).FirstOrDefaultAsync();
        doc.ShouldNotBeNull();
    }

    [Fact]
    public async Task handler_with_only_session_parameter_compiles_and_runs()
    {
        using var host = await BuildHost();
        var bus = host.Services.GetRequiredService<IMessageBus>();
        await bus.InvokeAsync(new SessionOnlyCommand(Guid.NewGuid()));
    }
}
