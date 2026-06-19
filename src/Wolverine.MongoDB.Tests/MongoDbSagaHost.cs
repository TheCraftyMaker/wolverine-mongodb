using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
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

                opts.CodeGeneration.GeneratedCodeOutputPath = AppContext.BaseDirectory.ParentDirectory()!
                    .ParentDirectory()!.ParentDirectory()!.AppendPath("Internal", "Generated");
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                // Discover only the saga type (its message handlers are all methods on it).
                // Unlike Cosmos, we do NOT IncludeAssembly the whole compliance assembly: that
                // assembly carries non-saga persistence handlers (e.g. the Todo side-effect) that
                // need a generic document-persistence provider. Cosmos claims those via an
                // unconditional CanPersist==true; this provider scopes CanPersist to Saga (R9), so
                // pulling them in would throw NoMatchingPersistenceProviderException at codegen.
                opts.Discovery.IncludeType<StringBasicWorkflow>();

                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync();
    }

    public Task<T?> LoadState<T>(Guid id) where T : Saga => throw new NotSupportedException();

    public Task<T?> LoadState<T>(int id) where T : Saga => throw new NotSupportedException();

    public Task<T?> LoadState<T>(long id) where T : Saga => throw new NotSupportedException();

    public async Task<T?> LoadState<T>(string id) where T : Saga
    {
        var collection = _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<T>(MongoConstants.SagaCollectionName(typeof(T)));
        return await collection.Find(Builders<T>.Filter.Eq("_id", id)).FirstOrDefaultAsync();
    }
}
