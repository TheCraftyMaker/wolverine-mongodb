using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;

namespace Wolverine.MongoDB.Tests;

[Collection("mongodb")]
public class node_persistence_compliance : NodePersistenceCompliance
{
    private readonly AppFixture _fixture;
    public node_persistence_compliance(AppFixture fixture) => _fixture = fixture;

    protected override async Task<IMessageStore> buildCleanMessageStore()
    {
        await _fixture.ClearAll();
        return _fixture.BuildMessageStore();
    }
}
