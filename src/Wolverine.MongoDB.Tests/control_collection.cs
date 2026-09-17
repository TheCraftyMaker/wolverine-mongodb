using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;
using Wolverine.Tracking;

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

    [Fact]
    public async Task an_expired_control_message_is_never_delivered()
    {
        await _fixture.ClearAll();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Balanced;
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Discovery.IncludeType(typeof(ExpiryProbeHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var envelope = new Envelope(new ExpiryProbe()) { Id = Guid.NewGuid(), MessageType = typeof(ExpiryProbe).FullName };
        envelope.Data = runtime.Options.DefaultSerializer.Write(envelope);
        var document = ControlMessageDocument.For(envelope, runtime.Options.UniqueNodeId,
            DateTime.UtcNow.AddSeconds(-5));

        await Database.GetCollection<ControlMessageDocument>(MongoConstants.ControlMessagesCollection)
            .InsertOneAsync(document, cancellationToken: TestContext.Current.CancellationToken);

        await Task.Delay(3.Seconds(), TestContext.Current.CancellationToken);

        ExpiryProbeHandler.Received.ShouldBe(0);
    }

    public record ExpiryProbe;

    public static class ExpiryProbeHandler
    {
        public static int Received;
        public static void Handle(ExpiryProbe probe) => Interlocked.Increment(ref Received);
    }
}
