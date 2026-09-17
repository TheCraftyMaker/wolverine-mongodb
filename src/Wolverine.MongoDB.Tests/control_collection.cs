using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;
using Wolverine.Transports.Tcp;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class control_collection
{
    private readonly AppFixture _fixture;
    public control_collection(AppFixture fixture) => _fixture = fixture;

    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    [Fact]
    public async Task balanced_host_provisions_the_control_collection_indexes()
    {
        await _fixture.ClearAll();
        await Database.DropCollectionAsync(MongoConstants.ControlMessagesCollection,
            TestContext.Current.CancellationToken);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                // Task 1 has no control transport yet, so an explicit endpoint keeps the host starting.
                opts.UseTcpForControlEndpoint();
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(TestContext.Current.CancellationToken);

        var indexes = await (await Database.GetCollection<BsonDocument>(MongoConstants.ControlMessagesCollection)
            .Indexes.ListAsync(TestContext.Current.CancellationToken)).ToListAsync(TestContext.Current.CancellationToken);

        indexes.ShouldContain(i => i["key"].AsBsonDocument.Contains("nodeId") && i["key"].AsBsonDocument.Contains("posted"));
        indexes.ShouldContain(i => i["key"].AsBsonDocument.Contains("expires") && i.Contains("expireAfterSeconds")
                                   && i["expireAfterSeconds"].ToInt64() == 0);
    }

    [Fact]
    public async Task solo_host_creates_no_control_collection()
    {
        await _fixture.ClearAll();
        await Database.DropCollectionAsync(MongoConstants.ControlMessagesCollection,
            TestContext.Current.CancellationToken);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync(TestContext.Current.CancellationToken);

        var names = await (await Database.ListCollectionNamesAsync(cancellationToken: TestContext.Current.CancellationToken))
            .ToListAsync(TestContext.Current.CancellationToken);
        names.ShouldNotContain(MongoConstants.ControlMessagesCollection);
    }

    [Fact]
    public void the_control_collection_name_is_reserved()
    {
        var options = new MongoDbPersistenceOptions();
        Should.Throw<ArgumentException>(
            () => options.MapEntityCollection<ReservedProbe>(MongoConstants.ControlMessagesCollection));
    }

    public class ReservedProbe
    {
        public Guid Id { get; set; }
    }
}
