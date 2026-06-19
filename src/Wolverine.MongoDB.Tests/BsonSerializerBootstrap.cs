using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Wolverine.MongoDB.Tests;

/// <summary>
/// Registers the process-wide Standard (subtype-4) <see cref="System.Guid"/> BSON representation
/// before any test serializes a Guid.
///
/// The library itself never mutates the host's BSON registry — every Guid property on its document
/// types carries an explicit <c>[BsonGuidRepresentation(GuidRepresentation.Standard)]</c> attribute,
/// so it round-trips correctly under the driver-3.x default (<c>Unspecified</c>). The saga
/// compliance suite, however, persists upstream saga POCOs (e.g. <c>GuidBasicWorkflow.Id</c>) whose
/// raw <c>Guid</c> identity members are un-annotated and live in the read-only Wolverine submodule.
/// A raw Guid cannot serialize while the global representation is <c>Unspecified</c>, so the test
/// host registers the same Standard representation the library annotations and the demo
/// (<c>InfrastructureBootstrap.ConfigureBsonSerializers</c>) already use. Standard everywhere means
/// this changes nothing for the library's own documents — it only fills in the global default the
/// un-annotated compliance types rely on.
///
/// Runs at assembly load (before any MongoClient or serialization) so it cannot lose the
/// "register before first use" race when the full suite runs.
/// </summary>
internal static class BsonSerializerBootstrap
{
    [ModuleInitializer]
    internal static void ConfigureGuidRepresentation()
    {
        try
        {
            BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        }
        catch (BsonSerializationException)
        {
            // A Guid serializer is already registered for this process — no-op.
        }
    }
}
