using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;

namespace Wolverine.MongoDB.Tests;

public class UowDoc
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}

public record UowWriteCommand(Guid Id, string Name);
public record UowFailingCommand(Guid Id);

public static class UowWriteHandler
{
    public static Task Handle(UowWriteCommand cmd, MongoDbUnitOfWork uow, CancellationToken ct)
        => uow.Collection<UowDoc>("uow_docs")
            .InsertOneAsync(new UowDoc { Id = cmd.Id.ToString(), Name = cmd.Name }, ct);
}

public static class UowFailingHandler
{
    public static async Task Handle(UowFailingCommand cmd, MongoDbUnitOfWork uow, CancellationToken ct)
    {
        await uow.Collection<UowDoc>("uow_docs")
            .InsertOneAsync(new UowDoc { Id = cmd.Id.ToString(), Name = "doomed" }, ct);
        throw new InvalidOperationException("fail after write");
    }
}

[Collection("mongodb")]
public class unit_of_work
{
    private readonly AppFixture _fixture;
    public unit_of_work(AppFixture fixture) => _fixture = fixture;

    private async Task<IHost> BuildHost()
    {
        await _fixture.ClearAll();
        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Policies.AutoApplyTransactions();
                opts.Discovery.IncludeType(typeof(UowWriteHandler));
                opts.Discovery.IncludeType(typeof(UowFailingHandler));
            }).StartAsync();
    }

    private IMongoCollection<UowDoc> Docs => _fixture.Client
        .GetDatabase(AppFixture.DatabaseName).GetCollection<UowDoc>("uow_docs");

    [Fact]
    public async Task writes_through_unit_of_work_commit_with_the_handler()
    {
        using var host = await BuildHost();
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var id = Guid.NewGuid();

        await bus.InvokeAsync(new UowWriteCommand(id, "hello"));

        (await Docs.Find(x => x.Id == id.ToString()).FirstOrDefaultAsync()).ShouldNotBeNull();
    }

    [Fact]
    public async Task writes_through_unit_of_work_roll_back_when_the_handler_throws()
    {
        using var host = await BuildHost();
        var bus = host.Services.GetRequiredService<IMessageBus>();
        var id = Guid.NewGuid();

        await Should.ThrowAsync<InvalidOperationException>(
            () => bus.InvokeAsync(new UowFailingCommand(id)));

        // The write went through the session-bound collection, so the abort
        // must have rolled it back.
        (await Docs.Find(x => x.Id == id.ToString()).FirstOrDefaultAsync()).ShouldBeNull();
    }
}
