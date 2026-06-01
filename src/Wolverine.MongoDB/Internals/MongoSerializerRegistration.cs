using System.Runtime.CompilerServices;
using System.Threading;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// Registers a <see cref="DateTimeOffsetSerializer"/> that persists every
/// <see cref="DateTimeOffset"/> field as a UTC BSON <c>Date</c> rather than the driver's
/// default <c>[ticks, offset]</c> array. The array representation breaks TTL indexes,
/// range filters (DLQ <c>SentAt</c>, scheduled <c>ExecutionTime</c>), and sort ordering.
///
/// Runs from a <c>[ModuleInitializer]</c> so it executes on assembly load — before any
/// store or collection is touched, including the unit tests that build a
/// <see cref="MongoDbMessageStore"/> directly. <see cref="WolverineMongoDbExtensions"/>
/// also calls it (idempotent) so host usage is covered regardless of module-init timing.
/// </summary>
internal static class MongoSerializerRegistration
{
    private static int _registered;

    // CA2255: a module initializer is exactly what's needed here — the BSON DateTimeOffset
    // serializer must be registered process-wide before ANY MongoDbMessageStore (including
    // directly-constructed ones in unit tests) serializes a document. The work is trivial,
    // guarded against double-registration, and exception-safe.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries",
        Justification = "Serializer must be registered on assembly load, before any direct-store usage.")]
    [ModuleInitializer]
    internal static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        try
        {
            BsonSerializer.RegisterSerializer(new DateTimeOffsetSerializer(BsonType.DateTime));
        }
        catch (BsonSerializationException)
        {
            // A serializer is already registered for DateTimeOffset; nothing to do.
        }
    }
}
