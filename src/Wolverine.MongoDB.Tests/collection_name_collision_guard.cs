using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.MongoDB.Tests
{
    /// <summary>
    /// Host-level coverage for the collection-name collision guard.
    ///
    /// <para>Collection names are derived from <c>Type.Name.ToLowerInvariant()</c>
    /// (<see cref="MongoConstants.SagaCollectionName"/> / <see cref="MongoConstants.EntityCollectionName"/>),
    /// which carries no namespace, no generic arguments and no case. Two Wolverine-persisted types with
    /// the same simple name therefore resolve to one collection and — with no <c>_t</c> discriminator and
    /// an unguarded upsert — silently mix or clobber each other's documents. These facts prove the guard
    /// fires from <c>HandlerGraph.Compile</c>, i.e. during <c>StartAsync</c> and before any listener
    /// starts, and that an explicit mapping is a working escape hatch.</para>
    ///
    /// <para><b>Isolation contract.</b> The naming registry is process-global and cannot be reset, so
    /// every fact here (a) owns its document types outright — no simple type name in this file is used
    /// anywhere else in the assembly — and (b) runs against its <b>own database name</b>, because both
    /// the override map and the claim registry are keyed by database. Nothing here can therefore reach
    /// the shared <see cref="AppFixture.DatabaseName"/> fixtures. The deliberately colliding handlers and
    /// saga types carry <c>[WolverineIgnore]</c> so conventional discovery in sibling suites never picks
    /// them up; the explicit <c>IncludeType</c> below still registers them
    /// (<c>HandlerDiscovery.FindCalls</c> concatenates the explicit types after the query).</para>
    /// </summary>
    [Collection("mongodb")]
    public class collection_name_collision_guard
    {
        private readonly AppFixture _fixture;

        public collection_name_collision_guard(AppFixture fixture) => _fixture = fixture;

        private IHost BuildHost(string databaseName, Action<MongoDbPersistenceOptions>? configure, params Type[] handlerTypes)
        {
            return Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Durability.Mode = DurabilityMode.Solo;

                    // Fresh compiled assembly per host (mirrors entity_identity_conventions).
                    opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

                    opts.Discovery.DisableConventionalDiscovery();
                    foreach (var handlerType in handlerTypes)
                    {
                        opts.Discovery.IncludeType(handlerType);
                    }

                    opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                    opts.UseMongoDbPersistence(databaseName, configure);
                    opts.Policies.AutoApplyTransactions();
                }).Build();
        }

        private IMongoCollection<BsonDocument> RawDocuments(string databaseName, string collectionName)
            => _fixture.Client.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);

        // ── two entity types, one collection ──────────────────────────────────────────

        /// <summary>
        /// <c>CollisionAlpha.Manifest</c> and <c>CollisionBeta.Manifest</c> both resolve to the
        /// un-prefixed entity collection <c>"manifest"</c>. Before the guard the host started happily
        /// and the two types' documents shared one collection, where an <c>IsUpsert = true</c> write
        /// with no version guard silently replaced a foreign document. The host must refuse to start.
        /// </summary>
        [Fact]
        public async Task two_entity_types_with_the_same_simple_name_fail_host_start()
        {
            const string database = "collision_guard_entity";
            using var host = BuildHost(database, configure: null,
                typeof(CollisionAlpha.AlphaManifestHandler),
                typeof(CollisionBeta.BetaManifestHandler));

            var ex = await Should.ThrowAsync<Exception>(() => host.StartAsync());
            var text = ex.ToString();

            text.ShouldContain("Wolverine.MongoDB.Tests.CollisionAlpha.Manifest");
            text.ShouldContain("Wolverine.MongoDB.Tests.CollisionBeta.Manifest");
            text.ShouldContain("manifest");
            text.ShouldContain(database);
            text.ShouldContain("MapEntityCollection");
        }

        // ── two saga types, one collection ────────────────────────────────────────────

        /// <summary>
        /// The saga variant is worse than the entity one: a shared <c>wolverine_saga_*</c> collection
        /// means a colliding start throws a duplicate-key error inside the transaction, and a retry can
        /// load the <i>other</i> saga type's document — at which point the version guard validates the
        /// corrupting write. Refuse at startup instead.
        /// </summary>
        [Fact]
        public async Task two_saga_types_with_the_same_simple_name_fail_host_start()
        {
            const string database = "collision_guard_saga";
            using var host = BuildHost(database, configure: null,
                typeof(CollisionAlpha.DispatchSaga),
                typeof(CollisionBeta.DispatchSaga));

            var ex = await Should.ThrowAsync<Exception>(() => host.StartAsync());
            var text = ex.ToString();

            text.ShouldContain("Wolverine.MongoDB.Tests.CollisionAlpha.DispatchSaga");
            text.ShouldContain("Wolverine.MongoDB.Tests.CollisionBeta.DispatchSaga");
            text.ShouldContain("wolverine_saga_dispatchsaga");
            text.ShouldContain(database);
            text.ShouldContain("MapSagaCollection");
        }

        // ── the no-regression pin at host level ───────────────────────────────────────

        /// <summary>
        /// An ordinary, non-conflicting entity type keeps its historical collection name — the
        /// un-prefixed, lowercased simple name resolved through
        /// <see cref="MongoConstants.EntityCollectionName"/>. Nothing is renamed or migrated by the
        /// guard. Passes before and after the change by design.
        /// </summary>
        [Fact]
        public async Task a_non_conflicting_entity_still_uses_its_default_collection_name()
        {
            const string database = "collision_guard_defaults";
            await _fixture.Client.DropDatabaseAsync(database);

            using var host = BuildHost(database, configure: null, typeof(CollisionDefaults.PlacardHandler));
            await host.StartAsync();

            var id = "PLACARD-" + Guid.NewGuid().ToString("N");
            await host.InvokeMessageAndWaitAsync(new CollisionDefaults.RecordPlacard(id, "yellow"));

            MongoConstants.EntityCollectionName(typeof(CollisionDefaults.Placard)).ShouldBe("placard");

            var documents = await RawDocuments(database, "placard")
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

            documents.Count.ShouldBe(1);
            documents[0]["_id"].AsString.ShouldBe(id);
            documents[0]["Colour"].AsString.ShouldBe("yellow");

            await host.StopAsync();
        }

        // ── the escape hatch: an explicit mapping ─────────────────────────────────────

        /// <summary>
        /// The same two colliding entity types, with one of them explicitly mapped. The host starts,
        /// and each type's documents land in its own collection — proving the mapping reaches the
        /// generated frames on both the codegen and the runtime leg. Read back as raw
        /// <see cref="BsonDocument"/>s so the assertion sees the actual on-disk shape and can tell that
        /// neither collection holds the other type's fields.
        /// </summary>
        [Fact]
        public async Task an_explicit_entity_mapping_lets_both_types_coexist()
        {
            const string database = "collision_guard_entity_mapping";
            const string mapped = "mapped_beta_waybill";
            await _fixture.Client.DropDatabaseAsync(database);

            using var host = BuildHost(database,
                o => o.MapEntityCollection<MappedBeta.Waybill>(mapped),
                typeof(MappedAlpha.AlphaWaybillHandler),
                typeof(MappedBeta.BetaWaybillHandler));

            await host.StartAsync();

            var alphaId = "WB-A-" + Guid.NewGuid().ToString("N");
            var betaId = "WB-B-" + Guid.NewGuid().ToString("N");
            await host.InvokeMessageAndWaitAsync(new MappedAlpha.RecordAlphaWaybill(alphaId, "Antwerp"));
            await host.InvokeMessageAndWaitAsync(new MappedBeta.RecordBetaWaybill(betaId, 12.5m));

            // Alpha keeps the default, un-prefixed name; nothing was renamed for it.
            MongoConstants.EntityCollectionName(typeof(MappedAlpha.Waybill)).ShouldBe("waybill");

            var alphaDocuments = await RawDocuments(database, "waybill")
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            alphaDocuments.Count.ShouldBe(1);
            alphaDocuments[0]["_id"].AsString.ShouldBe(alphaId);
            alphaDocuments[0]["Origin"].AsString.ShouldBe("Antwerp");
            alphaDocuments[0].Contains("Weight").ShouldBeFalse();

            var betaDocuments = await RawDocuments(database, mapped)
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            betaDocuments.Count.ShouldBe(1);
            betaDocuments[0]["_id"].AsString.ShouldBe(betaId);
            betaDocuments[0]["Weight"].ToDecimal().ShouldBe(12.5m);
            betaDocuments[0].Contains("Origin").ShouldBeFalse();

            await host.StopAsync();
        }

        /// <summary>
        /// The saga half of the escape hatch, and the proof that one resolver serves all three
        /// consumers: the generated saga frames write to the mapped collection, <c>ISagaStoreDiagnostics</c>
        /// reads it back (it builds its own database handle, so a resolver it did not honour would read
        /// the wrong collection), and <c>IMessageStoreAdmin.RebuildAsync</c>'s prefix sweep still drops it
        /// — which is exactly why <c>MapSagaCollection</c> requires the <c>wolverine_saga_</c> prefix.
        /// </summary>
        [Fact]
        public async Task an_explicit_saga_mapping_is_honoured_by_persistence_diagnostics_and_admin()
        {
            const string database = "collision_guard_saga_mapping";
            const string mapped = MongoConstants.SagaCollectionPrefix + "mapped_beta_consignment";
            await _fixture.Client.DropDatabaseAsync(database);

            using var host = BuildHost(database,
                o => o.MapSagaCollection<MappedBeta.Consignment>(mapped),
                typeof(MappedAlpha.Consignment),
                typeof(MappedBeta.Consignment));

            await host.StartAsync();

            var alphaId = "CN-A-" + Guid.NewGuid().ToString("N");
            var betaId = "CN-B-" + Guid.NewGuid().ToString("N");
            await host.InvokeMessageAndWaitAsync(new MappedAlpha.StartAlphaConsignment(alphaId));
            await host.InvokeMessageAndWaitAsync(new MappedBeta.StartBetaConsignment(betaId));

            // Alpha keeps the default name; only the mapped type moved.
            MongoConstants.SagaCollectionName(typeof(MappedAlpha.Consignment))
                .ShouldBe("wolverine_saga_consignment");

            var alphaDocuments = await RawDocuments(database, "wolverine_saga_consignment")
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            alphaDocuments.Count.ShouldBe(1);
            alphaDocuments[0]["_id"].AsString.ShouldBe(alphaId);
            alphaDocuments[0]["Status"].AsString.ShouldBe("alpha");

            var betaDocuments = await RawDocuments(database, mapped)
                .Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
            betaDocuments.Count.ShouldBe(1);
            betaDocuments[0]["_id"].AsString.ShouldBe(betaId);
            betaDocuments[0]["Notes"].AsString.ShouldBe("beta");

            // Diagnostics reads through the same resolver, so it finds the mapped saga (queried by
            // FullName — the short-name index is first-writer-wins for two same-short-named sagas).
            var diagnostics = host.GetRuntime().SagaStorage;
            var state = await diagnostics.ReadSagaAsync(
                typeof(MappedBeta.Consignment).FullName!, betaId, CancellationToken.None);
            state.ShouldNotBeNull();
            // "Notes" only exists on the beta saga, so this also proves the read did not land in the
            // alpha (default-named) collection.
            state.State.GetProperty("Notes").GetString().ShouldBe("beta");
            state.State.GetProperty("Id").GetString().ShouldBe(betaId);

            await host.StopAsync();

            // Admin's prefix sweep must still reach the mapped collection.
            await new MongoDbMessageStore(_fixture.Client, database, new WolverineOptions()).Admin.RebuildAsync();

            var remaining = await (await _fixture.Client.GetDatabase(database).ListCollectionNamesAsync())
                .ToListAsync();
            remaining.ShouldNotContain(mapped);
            remaining.ShouldNotContain("wolverine_saga_consignment");
        }
    }
}

// ── deliberately colliding fixtures ───────────────────────────────────────────────────
// Two namespaces, one simple name. The test project is otherwise a single flat namespace, so a
// namespace collision is not expressible without these blocks.

namespace Wolverine.MongoDB.Tests.CollisionAlpha
{
    /// <summary>Entity whose simple name collides with <c>CollisionBeta.Manifest</c>.</summary>
    public class Manifest
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Alpha-only payload, so a cross-type read is observable.</summary>
        public string? Carrier { get; set; }
    }

    /// <summary>Command recording an alpha manifest.</summary>
    public record RecordAlphaManifest(string Id, string Carrier);

    // Deliberately colliding: the facts assert the host FAILS to start. [WolverineIgnore] keeps it out
    // of conventional discovery so it cannot poison sibling suites in this assembly.
    [WolverineIgnore]
    public static class AlphaManifestHandler
    {
        public static Store<Manifest> Handle(RecordAlphaManifest msg)
            => Storage.Store(new Manifest { Id = msg.Id, Carrier = msg.Carrier });
    }

    /// <summary>Saga whose simple name collides with <c>CollisionBeta.DispatchSaga</c>.</summary>
    [WolverineIgnore]
    public class DispatchSaga : Saga
    {
        /// <summary>Saga identity.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Saga state.</summary>
        public string? Status { get; set; }

        public static DispatchSaga Start(StartAlphaDispatch msg)
            => new() { Id = msg.Id, Status = "alpha" };
    }

    /// <summary>Command starting an alpha dispatch saga.</summary>
    public record StartAlphaDispatch(string Id);
}

namespace Wolverine.MongoDB.Tests.CollisionBeta
{
    /// <summary>Entity whose simple name collides with <c>CollisionAlpha.Manifest</c>.</summary>
    public class Manifest
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Beta-only payload, so a cross-type read is observable.</summary>
        public decimal Amount { get; set; }
    }

    /// <summary>Command recording a beta manifest.</summary>
    public record RecordBetaManifest(string Id, decimal Amount);

    // Deliberately colliding — see the CollisionAlpha note.
    [WolverineIgnore]
    public static class BetaManifestHandler
    {
        public static Store<Manifest> Handle(RecordBetaManifest msg)
            => Storage.Store(new Manifest { Id = msg.Id, Amount = msg.Amount });
    }

    /// <summary>Saga whose simple name collides with <c>CollisionAlpha.DispatchSaga</c>.</summary>
    [WolverineIgnore]
    public class DispatchSaga : Saga
    {
        /// <summary>Saga identity.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Saga state.</summary>
        public string? Notes { get; set; }

        public static DispatchSaga Start(StartBetaDispatch msg)
            => new() { Id = msg.Id, Notes = "beta" };
    }

    /// <summary>Command starting a beta dispatch saga.</summary>
    public record StartBetaDispatch(string Id);
}

namespace Wolverine.MongoDB.Tests.MappedAlpha
{
    /// <summary>Entity keeping the default collection name in the mapping fact.</summary>
    public class Waybill
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Alpha-only payload.</summary>
        public string? Origin { get; set; }
    }

    /// <summary>Command recording an alpha waybill.</summary>
    public record RecordAlphaWaybill(string Id, string Origin);

    // Colliding by simple name with MappedBeta.Waybill — hidden from conventional discovery so only
    // this file's host (which supplies the mapping) ever sees it.
    [WolverineIgnore]
    public static class AlphaWaybillHandler
    {
        public static Store<Waybill> Handle(RecordAlphaWaybill msg)
            => Storage.Store(new Waybill { Id = msg.Id, Origin = msg.Origin });
    }

    /// <summary>Saga keeping the default collection name in the saga mapping fact.</summary>
    [WolverineIgnore]
    public class Consignment : Saga
    {
        /// <summary>Saga identity.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Saga state.</summary>
        public string? Status { get; set; }

        public static Consignment Start(StartAlphaConsignment msg)
            => new() { Id = msg.Id, Status = "alpha" };
    }

    /// <summary>Command starting an alpha consignment saga.</summary>
    public record StartAlphaConsignment(string Id);
}

namespace Wolverine.MongoDB.Tests.MappedBeta
{
    /// <summary>Entity given an explicit collection name in the mapping fact.</summary>
    public class Waybill
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Beta-only payload.</summary>
        public decimal Weight { get; set; }
    }

    /// <summary>Command recording a beta waybill.</summary>
    public record RecordBetaWaybill(string Id, decimal Weight);

    // Colliding by simple name with MappedAlpha.Waybill — see the MappedAlpha note.
    [WolverineIgnore]
    public static class BetaWaybillHandler
    {
        public static Store<Waybill> Handle(RecordBetaWaybill msg)
            => Storage.Store(new Waybill { Id = msg.Id, Weight = msg.Weight });
    }

    /// <summary>Saga given an explicit collection name in the saga mapping fact.</summary>
    [WolverineIgnore]
    public class Consignment : Saga
    {
        /// <summary>Saga identity.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Saga state.</summary>
        public string? Notes { get; set; }

        public static Consignment Start(StartBetaConsignment msg)
            => new() { Id = msg.Id, Notes = "beta" };
    }

    /// <summary>Command starting a beta consignment saga.</summary>
    public record StartBetaConsignment(string Id);
}

namespace Wolverine.MongoDB.Tests.CollisionDefaults
{
    /// <summary>An ordinary, non-conflicting entity — the "nothing was renamed" pin.</summary>
    public class Placard
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Payload.</summary>
        public string? Colour { get; set; }
    }

    /// <summary>Command recording a placard.</summary>
    public record RecordPlacard(string Id, string Colour);

    // Not colliding, but still hidden from conventional discovery: sibling suites that scan this
    // assembly would otherwise create a "placard" collection in the shared fixture database.
    [WolverineIgnore]
    public static class PlacardHandler
    {
        public static Store<Placard> Handle(RecordPlacard msg)
            => Storage.Store(new Placard { Id = msg.Id, Colour = msg.Colour });
    }
}
