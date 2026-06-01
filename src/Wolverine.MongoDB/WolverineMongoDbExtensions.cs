using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Sagas;

namespace Wolverine.MongoDB;

public static class WolverineMongoDbExtensions
{
    /// <summary>
    /// Use MongoDB for Wolverine's transactional inbox/outbox message storage.
    /// Requires an <see cref="IMongoClient"/> to be registered in the application's
    /// container. The MongoDB server must be running as a replica set so that
    /// multi-document transactions are available.
    /// </summary>
    /// <param name="options"></param>
    /// <param name="databaseName">The MongoDB database name to use</param>
    /// <returns></returns>
    public static WolverineOptions UseMongoDbPersistence(this WolverineOptions options, string databaseName)
    {
        // Idempotent (guarded); ensures the UTC DateTimeOffset serializer is registered for
        // host usage even if module-initializer timing ever differs.
        MongoSerializerRegistration.Register();

        options.Services.AddSingleton<IMessageStore>(sp =>
        {
            var client = sp.GetRequiredService<IMongoClient>();
            var wolverineOptions = sp.GetRequiredService<WolverineOptions>();
            return new MongoDbMessageStore(client, databaseName, wolverineOptions);
        });

        // Register the MongoDB database for use by code-generated handlers
        options.Services.AddSingleton(sp =>
            sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));

        options.CodeGeneration.InsertFirstPersistenceStrategy<MongoDbPersistenceFrameProvider>();
        options.CodeGeneration.ReferenceAssembly(typeof(WolverineMongoDbExtensions).Assembly);
        return options;
    }
}
