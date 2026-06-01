using Microsoft.Extensions.DependencyInjection;
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
        services.AddSingleton<IMongoClient>(_ => new MongoClient(mongoConnectionString));

        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<OrderSummaryRepository>();

        return services;
    }
}
