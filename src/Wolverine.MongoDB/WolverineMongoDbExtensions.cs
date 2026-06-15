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
    /// write to commit atomically with those envelopes, every write must run on that session.
    /// </para>
    /// <para>
    /// The recommended handler write surface is <see cref="MongoDbUnitOfWork"/>: accept it as a
    /// handler parameter and the generated frame threads the active
    /// <see cref="IClientSessionHandle"/> into every write performed through
    /// <see cref="MongoDbUnitOfWork.Collection{T}"/>, making it impossible to forget the session.
    /// The raw <see cref="IClientSessionHandle"/> parameter pattern remains valid for
    /// repository-based handlers: accept the generated session and pass it to every MongoDB
    /// write you perform (for example <c>collection.InsertOneAsync(session, doc)</c>).
    /// </para>
    /// <para>
    /// The <see cref="IMongoDatabase"/> registered by this method does NOT auto-enlist in the
    /// transaction. A handler that resolves the database (or a collection) and writes WITHOUT
    /// the session writes outside the transaction, so its domain write is NOT atomic with the
    /// outbox and can be lost or duplicated on failure.
    /// </para>
    /// </remarks>
    /// <param name="options"></param>
    /// <param name="databaseName">The MongoDB database name to use</param>
    /// <param name="configure">Optional callback to tune MongoDB-specific persistence options such as lock lease duration</param>
    /// <returns></returns>
    public static WolverineOptions UseMongoDbPersistence(this WolverineOptions options, string databaseName,
        Action<MongoDbPersistenceOptions>? configure = null)
    {
        var persistenceOptions = new MongoDbPersistenceOptions();
        configure?.Invoke(persistenceOptions);

        options.Services.AddSingleton<IMessageStore>(sp =>
        {
            var client = sp.GetRequiredService<IMongoClient>();
            var wolverineOptions = sp.GetRequiredService<WolverineOptions>();
            return new MongoDbMessageStore(client, databaseName, wolverineOptions, persistenceOptions);
        });

        // Register the MongoDB database for use by code-generated handlers
        options.Services.AddSingleton(sp =>
            sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));

        options.CodeGeneration.InsertFirstPersistenceStrategy<MongoDbPersistenceFrameProvider>();
        options.CodeGeneration.ReferenceAssembly(typeof(WolverineMongoDbExtensions).Assembly);
        return options;
    }
}
