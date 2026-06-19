using Wolverine.ComplianceTests.Sagas;

namespace Wolverine.MongoDB.Tests;

// Serialized with the other "mongodb" tests so the compliance facts don't race them on the
// shared wolverine_tests database / Testcontainer.
[Collection("mongodb")]
public class string_saga_storage_compliance : StringIdentifiedSagaComplianceSpecs<MongoDbSagaHost>
{
    public string_saga_storage_compliance()
    {
    }
}
