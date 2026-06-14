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
    /// <remarks>
    /// <para>
    /// <b>Domain-write atomicity contract.</b> The "domain write + outbox write in one
    /// transaction" guarantee only holds when the handler enlists its own MongoDB writes in
    /// the Wolverine-managed transaction. Wolverine opens an <see cref="IClientSessionHandle"/>
    /// for the handler and persists outgoing/inbox envelopes on that session; for a domain
    /// write to commit atomically with those envelopes, the handler MUST accept the generated
    /// <see cref="IClientSessionHandle"/> as a parameter and pass it to every MongoDB write it
    /// performs (for example <c>collection.InsertOneAsync(session, doc)</c>).
    /// </para>
    /// <para>
    /// The <see cref="IMongoDatabase"/> registered by this method does NOT auto-enlist in the
    /// transaction. A handler that resolves the database (or a collection) and writes WITHOUT
    /// the session writes outside the transaction, so its domain write is NOT atomic with the
    /// outbox and can be lost or duplicated on failure.
    /// </para>
    /// <para>
    /// A session-bound write helper that removes the need to thread the session manually is a
    /// planned future enhancement (follow-up).
    /// </para>
    /// </remarks>
    /// <param name="options"></param>
    /// <param name="databaseName">The MongoDB database name to use</param>
    /// <returns></returns>
    public static WolverineOptions UseMongoDbPersistence(this WolverineOptions options, string databaseName)
    {
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
