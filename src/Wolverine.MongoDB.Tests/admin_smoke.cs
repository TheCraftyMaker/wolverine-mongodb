using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;

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

    [Fact]
    public async Task migrate_creates_the_expected_indexes()
    {
        var store = _fixture.BuildMessageStore();
        await store.Admin.RebuildAsync();

        var incomingIndexes = await (await store.Incoming.Indexes.ListAsync()).ToListAsync();
        var names = incomingIndexes.Select(i => i["name"].AsString).ToList();

        // Print actual names so any naming mismatch is immediately visible in CI output.
        foreach (var n in names) Console.WriteLine($"incoming index: {n}");

        names.ShouldContain("status_1_executionTime_1");
        names.ShouldContain("envelopeId_1");
        names.ShouldContain("keepUntil_1");
    }
}
