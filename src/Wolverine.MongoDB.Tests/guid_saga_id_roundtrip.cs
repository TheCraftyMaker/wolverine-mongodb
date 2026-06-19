using Shouldly;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.MongoDB.Tests;

/// <summary>
/// Focused regression for S7: a Guid-identified saga must persist its identity as a native BSON
/// Guid <c>_id</c>, not a string. The compliance suite proves the saga round-trips; this test
/// pins down the <i>representation</i> so a future regression to the S6 string baseline (or an
/// accidental ToString of the id) is caught directly.
/// </summary>
[Collection("mongodb")]
public class guid_saga_id_roundtrip
{
    [Fact]
    public async Task guid_id_round_trips_as_a_native_guid_not_a_string()
    {
        var sagaHost = new MongoDbSagaHost();
        using var host = await sagaHost.BuildHostAsync<GuidBasicWorkflow>();

        var id = Guid.NewGuid();
        await host.InvokeMessageAndWaitAsync(new GuidStart { Id = id, Name = "Croaker" });

        // Loading by the native Guid _id round-trips the saga document.
        var byGuid = await sagaHost.LoadState<GuidBasicWorkflow>(id);
        byGuid.ShouldNotBeNull();
        byGuid.Id.ShouldBe(id);
        byGuid.Name.ShouldBe("Croaker");

        // The same id rendered as a string matches nothing — proof the _id was written as a
        // native Guid (BSON binary), not a string. A string-pinned baseline would match here.
        (await sagaHost.LoadState<GuidBasicWorkflow>(id.ToString())).ShouldBeNull();
    }
}
