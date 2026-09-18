using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Wolverine;
using Wolverine.Tracking;

namespace OrderDemo.IntegrationTests;

/// <summary>
/// Proves the consumer-visible half of Wolverine.MongoDB's native <c>mongocontrol</c>
/// transport (1.1.0): a <see cref="DurabilityMode.Balanced"/> host needs no control-endpoint
/// configuration to run, and only a <see cref="DurabilityMode.Balanced"/> host provisions the
/// control collection. The library's own tests cover the transport's internals
/// (<c>control_collection.cs</c>, <c>durability_mode_guard.cs</c>); this suite only checks what
/// the demo, as a packaged-nupkg consumer, actually depends on.
/// </summary>
[Collection("orders")]
public class ControlTransportTests(OrdersFixture fixture)
{
    // MongoConstants.ControlMessagesCollection is internal to the library, so the exact value is
    // reproduced here, mirroring OrderNoteFlowTests' NoteCollection constant.
    private const string ControlCollection = "wolverine_control_messages";

    [Fact]
    public async Task Balanced_Host_Gets_A_Control_Endpoint_Without_Any_Configuration()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db, DurabilityMode.Balanced);

        var runtime = host.GetRuntime();
        var endpoint = runtime.Options.Transports.NodeControlEndpoint;

        endpoint.Should().NotBeNull("a Balanced host must get a control endpoint with no configuration on 1.1.0");
        endpoint!.Uri.Scheme.Should().Be("mongocontrol");
        endpoint.Uri.Host.Should().Be(runtime.Options.UniqueNodeId.ToString());
    }

    [Fact]
    public async Task Balanced_Host_Provisions_The_Control_Collection_Indexes()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db, DurabilityMode.Balanced);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var indexes = await (await mongo.GetCollection<BsonDocument>(ControlCollection).Indexes.ListAsync())
            .ToListAsync();

        indexes.Should().Contain(i => i["key"].AsBsonDocument.Contains("nodeId") && i["key"].AsBsonDocument.Contains("posted"),
            "the collection needs a compound index on nodeId + posted for the per-node poll");
        indexes.Should().Contain(i => i["key"].AsBsonDocument.Contains("expires")
                                       && i.Contains("expireAfterSeconds") && i["expireAfterSeconds"].ToInt64() == 0,
            "expired control messages are reaped by a TTL index on expires");
    }

    [Fact]
    public async Task Solo_Host_Creates_No_Control_Collection()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var names = await (await mongo.ListCollectionNamesAsync()).ToListAsync();

        names.Should().NotContain(ControlCollection, "a Solo host has no control transport to provision it for");
    }
}
