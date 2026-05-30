using Shouldly;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class admin_smoke
{
    private readonly AppFixture _fixture;
    public admin_smoke(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task rebuild_then_connectivity_and_counts()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();
        await store.Admin.CheckConnectivityAsync(CancellationToken.None);

        var counts = await store.Admin.FetchCountsAsync();
        counts.Incoming.ShouldBe(0);
        counts.Outgoing.ShouldBe(0);
        counts.DeadLetter.ShouldBe(0);
    }
}
