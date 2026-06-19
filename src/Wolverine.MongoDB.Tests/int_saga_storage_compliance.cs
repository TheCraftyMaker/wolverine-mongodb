using Wolverine.ComplianceTests.Sagas;

namespace Wolverine.MongoDB.Tests;

// Serialized with the other "mongodb" tests so the compliance facts don't race them on the
// shared wolverine_tests database / Testcontainer. Proves native int saga _id support (S7).
[Collection("mongodb")]
public class int_saga_storage_compliance : IntIdentifiedSagaComplianceSpecs<MongoDbSagaHost>
{
    public int_saga_storage_compliance()
    {
    }
}
