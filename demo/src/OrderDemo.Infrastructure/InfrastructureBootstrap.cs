using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using OrderDemo.Infrastructure.Persistence;

namespace OrderDemo.Infrastructure;

public static class InfrastructureBootstrap
{
    /// <summary>
    /// Registers the MongoDB client and domain repositories.
    ///
    /// NOTE: <c>IMongoDatabase</c> is intentionally NOT registered here.
    /// It is registered by <c>opts.UseMongoDbPersistence(databaseName)</c> in
    /// Program.cs so that Wolverine's outbox and the application repositories
    /// share the exact same database binding (and thus the same IMongoClient).
    /// </summary>
    public static IServiceCollection AddOrderDemoInfrastructure(
        this IServiceCollection services,
        string mongoConnectionString)
    {
        // Register standard Guid representation so that domain aggregates with Guid
        // primary keys are stored as BSON UUID (subtype 4) rather than the legacy
        // C# BSON UUID (subtype 3). Must run before any collection is accessed.
        ConfigureBsonSerializers();

        services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnectionString));

        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<OrderSummaryRepository>();

        return services;
    }

    /// <summary>
    /// Registers process-wide BSON serializers. Idempotent — safe to call multiple times.
    /// Call this before creating any MongoClient to ensure consistent serialization.
    /// </summary>
    public static void ConfigureBsonSerializers()
    {
        try
        {
            BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        }
        catch (BsonSerializationException)
        {
            // Already registered — no-op (can happen if called more than once in process)
        }
    }
}
