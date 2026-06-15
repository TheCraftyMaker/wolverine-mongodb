using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class leader_lease
{
    private readonly AppFixture _fixture;
    public leader_lease(AppFixture fixture) => _fixture = fixture;

    // Each WolverineOptions instance is born with its own UniqueNodeId, so two
    // stores built this way naturally represent two distinct nodes.
    private MongoDbMessageStore BuildStore(TimeSpan lease)
        => new(_fixture.Client, AppFixture.DatabaseName, new WolverineOptions(),
            new MongoDbPersistenceOptions { LockLeaseDuration = lease });

    [Fact]
    public async Task second_node_takes_over_after_the_lease_expires()
    {
        await _fixture.ClearAll();
        var nodeA = BuildStore(TimeSpan.FromSeconds(2));
        var nodeB = BuildStore(TimeSpan.FromSeconds(2));

        (await nodeA.TryAttainLeadershipLockAsync(CancellationToken.None)).ShouldBeTrue();
        (await nodeB.TryAttainLeadershipLockAsync(CancellationToken.None)).ShouldBeFalse(
            "the lease is live, a second node must not steal it");

        await Task.Delay(TimeSpan.FromSeconds(3));

        (await nodeB.TryAttainLeadershipLockAsync(CancellationToken.None)).ShouldBeTrue(
            "an expired lease must be claimable by another node");
        nodeA.HasLeadershipLock().ShouldBeFalse(
            "the deposed node must not still believe it is leader");
    }

    [Fact]
    public async Task has_leadership_lock_goes_false_before_the_lease_fully_expires()
    {
        await _fixture.ClearAll();
        var node = BuildStore(TimeSpan.FromSeconds(2));

        (await node.TryAttainLeadershipLockAsync(CancellationToken.None)).ShouldBeTrue();
        node.HasLeadershipLock().ShouldBeTrue();

        // At 75% of a 2s lease (1.5s), the cached claim must already be reported
        // as lost so the node stops acting as leader before takeover is possible.
        await Task.Delay(TimeSpan.FromMilliseconds(1700));
        node.HasLeadershipLock().ShouldBeFalse();
    }
}
