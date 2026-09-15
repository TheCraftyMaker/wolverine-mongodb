using Shouldly;
using Wolverine.Runtime.Agents;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class node_heartbeat
{
    [Fact]
    public async Task delete_old_node_records_keeps_only_the_newest_n()
    {
        await _fixture.ClearAll();
        var store = _fixture.BuildMessageStore();

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var i = 0; i < 10; i++)
        {
            await store.Nodes.LogRecordsAsync(new NodeRecord
            {
                NodeNumber = 1,
                RecordType = NodeRecordType.NodeStarted,
                Timestamp = baseTime.AddSeconds(i),
                Description = $"record {i}",
                ServiceName = "test"
            });
        }

        await store.Nodes.DeleteOldNodeRecordsAsync(3);

        var remaining = await store.Nodes.FetchRecentRecordsAsync(100);
        remaining.Count.ShouldBe(3);
        remaining.Select(x => x.Description).ShouldBe(new[] { "record 7", "record 8", "record 9" });
    }


    private readonly AppFixture _fixture;
    public node_heartbeat(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task heartbeat_for_unknown_node_reports_absence_and_creates_nothing()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();
        await store.Nodes.ClearAllAsync(CancellationToken.None);

        var unknown = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            ControlUri = new Uri("dbcontrol://unknown"),
            Description = "newcomer"
        };

        // WolverineFx 6.38 contract: a heartbeat for a node without a document reports the miss and
        // MUST NOT insert one. NodeAgentController re-registers the node's real identity itself
        // (ReregisterNodeAsync) so no skeleton with a fresh number and empty capabilities is written.
        (await store.Nodes.MarkHealthCheckAsync(unknown, CancellationToken.None)).ShouldBeFalse();

        (await store.Nodes.LoadNodeAsync(unknown.NodeId, CancellationToken.None)).ShouldBeNull();
        (await store.Nodes.LoadAllNodesAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task heartbeat_updates_known_node()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();
        await store.Nodes.ClearAllAsync(CancellationToken.None);

        var node = new WolverineNode
        {
            NodeId = Guid.NewGuid(),
            ControlUri = new Uri("dbcontrol://known"),
            Description = "real"
        };

        await store.Nodes.PersistAsync(node, CancellationToken.None);

        // Reports the hit and leaves exactly the one registered node.
        (await store.Nodes.MarkHealthCheckAsync(node, CancellationToken.None)).ShouldBeTrue();

        var reloaded = await store.Nodes.LoadNodeAsync(node.NodeId, CancellationToken.None);
        reloaded.ShouldNotBeNull();
        (await store.Nodes.LoadAllNodesAsync(CancellationToken.None)).Count.ShouldBe(1);
    }
}
