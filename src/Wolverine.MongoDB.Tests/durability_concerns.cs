using MongoDB.Driver;
using Shouldly;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class durability_concerns
{
    private readonly AppFixture _fixture;
    public durability_concerns(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public void store_collections_use_majority_journaled_write_concern_and_majority_reads()
    {
        var store = _fixture.BuildMessageStore();

        var expectedWrite = WriteConcern.WMajority.With(journal: true);
        store.Incoming.Settings.WriteConcern.ShouldBe(expectedWrite);
        store.Outgoing.Settings.WriteConcern.ShouldBe(expectedWrite);
        store.DeadLetterDocs.Settings.WriteConcern.ShouldBe(expectedWrite);

        store.Incoming.Settings.ReadConcern.ShouldBe(ReadConcern.Majority);
        store.Outgoing.Settings.ReadConcern.ShouldBe(ReadConcern.Majority);
    }
}
