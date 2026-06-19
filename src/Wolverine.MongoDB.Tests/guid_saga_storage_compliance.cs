using Wolverine.ComplianceTests.Sagas;

namespace Wolverine.MongoDB.Tests;

// Serialized with the other "mongodb" tests so the compliance facts don't race them on the
// shared wolverine_tests database / Testcontainer. Proves native Guid saga _id support (S7).
[Collection("mongodb")]
public class guid_saga_storage_compliance : GuidIdentifiedSagaComplianceSpecs<MongoDbSagaHost>
{
    public guid_saga_storage_compliance()
    {
    }
}
