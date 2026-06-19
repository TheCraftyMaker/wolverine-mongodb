using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.MongoDB.Internals;

namespace Wolverine.MongoDB.Tests;

/// <summary>
/// <see cref="ISagaHost"/> implementation for the MongoDB saga compliance suite. Mirrors
/// Wolverine's <c>CosmosDbSagaHost</c>: Solo durability, runtime codegen written to an output
/// path for inspection, explicit saga-type discovery, and a <c>LoadState</c> that reads the saga
/// document directly from MongoDB (independent of Wolverine) to verify persistence.
/// </summary>
public class MongoDbSagaHost : ISagaHost
{
    private readonly AppFixture _fixture;

    public MongoDbSagaHost()
    {
        _fixture = new AppFixture();
        _fixture.InitializeAsync().GetAwaiter().GetResult();
    }

    public async Task<IHost> BuildHostAsync<TSaga>()
    {
        // The compliance fixture reuses a single database, so clear it (dropping the per-saga-type
        // collections too) before each host build to keep facts independent.
        await _fixture.ClearAll();

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Dynamic codegen (the default, and what Wolverine's own multi-id-type
                // SqlServerSagaHost uses). Auto mode + a shared GeneratedCodeOutputPath cannot be
                // used here: the generated handler type name is keyed off the *message* type
                // (e.g. CompleteOneHandler<hash>), which is identical across the String/Guid/int/
                // long workflows because they share message types. Under Auto, the first compiled
                // handler is reused in-memory by name for every later host, so the long/Guid specs
                // would load a StringBasicWorkflow saga. Dynamic compiles a fresh assembly per host.
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

                // Discover only the saga type under test (its message handlers are all methods on
                // it). Parameterized on TSaga so the same host serves the string, Guid, int and
                // long compliance suites. Unlike Cosmos, we do NOT IncludeAssembly the whole
                // compliance assembly: that assembly carries non-saga persistence handlers (e.g.
                // the Todo side-effect) that need a generic document-persistence provider. Cosmos
                // claims those via an unconditional CanPersist==true; this provider scopes
                // CanPersist to Saga (R9), so pulling them in would throw
                // NoMatchingPersistenceProviderException at codegen.
                opts.Discovery.IncludeType(typeof(TSaga));

                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync();
    }

    public Task<T?> LoadState<T>(Guid id) where T : Saga => LoadById<T, Guid>(id);

    public Task<T?> LoadState<T>(int id) where T : Saga => LoadById<T, int>(id);

    public Task<T?> LoadState<T>(long id) where T : Saga => LoadById<T, long>(id);

    public Task<T?> LoadState<T>(string id) where T : Saga => LoadById<T, string>(id);

    // Reads the saga document directly from MongoDB by its typed _id, independent of Wolverine, to
    // verify persistence. TId is kept strongly typed (not boxed to object) so the _id filter uses
    // the same native serializer the saga frame used when writing — Guid/int/long/string match
    // exactly rather than risking the object serializer choosing a different BSON representation.
    private async Task<T?> LoadById<T, TId>(TId id) where T : Saga
    {
        var collection = _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<T>(MongoConstants.SagaCollectionName(typeof(T)));
        return await collection.Find(Builders<T>.Filter.Eq("_id", id)).FirstOrDefaultAsync();
    }
}
