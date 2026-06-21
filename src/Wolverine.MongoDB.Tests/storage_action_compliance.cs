using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Wolverine.ComplianceTests;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Durability;
using Xunit;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// Tier 1 acceptance oracle: the upstream <see cref="StorageActionCompliance"/> suite run against a
/// real MongoDB replica set through the actual generated entity frames. Proves the generic
/// <c>[Entity]</c> parameter-load and <c>Insert&lt;T&gt;</c>/<c>Update&lt;T&gt;</c>/<c>Store&lt;T&gt;</c>/
/// <c>Delete&lt;T&gt;</c>/<c>IStorageAction&lt;T&gt;</c>/<c>UnitOfWork&lt;T&gt;</c> return-value side effects
/// persist through MongoDB inside the outbox transaction.
///
/// <para><see cref="StorageActionCompliance"/> is configured via <see cref="configureWolverine"/> /
/// <see cref="Load"/> / <see cref="Persist"/> overrides (it is not generic over an
/// <c>ISagaHost</c>), so there is no separate host file. <see cref="Load"/>/<see cref="Persist"/>
/// target the SAME collection the generated frames write to —
/// <c>MongoConstants.EntityCollectionName(typeof(Todo))</c> (= <c>"todo"</c>) — never a hard-coded
/// literal (D6 Decision 4a). Mirrors RavenDb's <c>using_storage_return_types_and_entity_attributes</c>.</para>
/// </summary>
[Collection("mongodb")]
public class storage_action_compliance : StorageActionCompliance
{
    private readonly AppFixture _fixture;

    public storage_action_compliance(AppFixture fixture) => _fixture = fixture;

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
        opts.UseMongoDbPersistence(AppFixture.DatabaseName);
        opts.Policies.AutoApplyTransactions();
        opts.Durability.Mode = DurabilityMode.Solo;

        // Fresh compiled assembly per host (mirrors the saga compliance host): avoids cross-host
        // in-memory handler reuse from a shared, message-type-keyed Auto codegen name.
        opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
    }

    // Each fact assumes a clean slate. The entity collection is NOT a Wolverine system collection, so
    // ClearAllAsync deliberately does not touch it (D6 Decision 4c) — clear it here instead. The first
    // write inside a transaction recreates it (mongo:7 supports transactional collection creation, the
    // same mechanism the saga collections already rely on).
    protected override Task initialize()
        => _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .DropCollectionAsync(MongoConstants.EntityCollectionName(typeof(Todo)));

    private IMongoCollection<Todo> Collection
        => _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<Todo>(MongoConstants.EntityCollectionName(typeof(Todo)));

    public override Task<Todo?> Load(string id)
        => Collection.Find(Builders<Todo>.Filter.Eq("_id", id)).FirstOrDefaultAsync()!;

    public override Task Persist(Todo todo)
        => Collection.ReplaceOneAsync(
            Builders<Todo>.Filter.Eq("_id", todo.Id), todo, new ReplaceOptions { IsUpsert = true });
}
