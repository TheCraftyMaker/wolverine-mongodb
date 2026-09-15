using System.Collections.Concurrent;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// The MongoDB provider claims every entity type through <c>CanPersist</c>, so it is a catch-all
/// document store in the sense of <see cref="IPersistenceFrameProvider.IsCatchAll"/> — and must say so,
/// or in a mixed-persistence application it is consulted <em>before</em> a selective provider (EF Core
/// only claims the types mapped in a registered <c>DbContext</c>) whenever it happens to be registered
/// later, stealing that provider's entities. Wolverine sorts catch-alls after selective providers
/// (<c>GenerationRulesExtensions.OrderedPersistenceProviders</c>) precisely so registration order stops
/// mattering; that only works for providers that advertise the flag.
/// </summary>
[Collection("mongodb")]
public class persistence_provider_precedence
{
    private readonly AppFixture _fixture;
    public persistence_provider_precedence(AppFixture fixture) => _fixture = fixture;

    private static GenerationRules rulesWith(params IPersistenceFrameProvider[] providers)
    {
        var rules = new GenerationRules();
        rules.Properties[GenerationRulesExtensions.PersistenceKey] = providers.ToList();
        return rules;
    }

    [Fact]
    public void the_mongodb_provider_advertises_itself_as_a_catch_all()
    {
        // Through the interface on purpose: IsCatchAll is a default interface member, so reading it
        // off the concrete type only compiles when the override exists. Through the interface it
        // reads the default (false) when the override is missing — which is the regression.
        ((IPersistenceFrameProvider)new MongoDbPersistenceFrameProvider()).IsCatchAll.ShouldBeTrue();
    }

    [Fact]
    public void a_selective_provider_wins_its_entity_when_mongodb_registered_after_it()
    {
        var selective = new SelectiveProvider(typeof(SelectiveEntity));
        var mongo = new MongoDbPersistenceFrameProvider();

        // UseMongoDbPersistence uses InsertFirstPersistenceStrategy, so registering MongoDB LAST puts
        // it at index 0 — the raw order that used to steal the entity from the selective provider.
        var rules = rulesWith(mongo, selective);

        rules.TryFindPersistenceFrameProvider(null!, typeof(SelectiveEntity), out var provider).ShouldBeTrue();
        provider.ShouldBeSameAs(selective);
    }

    [Fact]
    public void a_selective_provider_wins_its_entity_when_mongodb_registered_before_it()
    {
        var selective = new SelectiveProvider(typeof(SelectiveEntity));
        var mongo = new MongoDbPersistenceFrameProvider();

        var rules = rulesWith(selective, mongo);

        rules.TryFindPersistenceFrameProvider(null!, typeof(SelectiveEntity), out var provider).ShouldBeTrue();
        provider.ShouldBeSameAs(selective);
    }

    [Fact]
    public void an_unmapped_entity_still_falls_through_to_mongodb_in_both_orders()
    {
        var selective = new SelectiveProvider(typeof(SelectiveEntity));
        var mongo = new MongoDbPersistenceFrameProvider();

        foreach (var rules in new[] { rulesWith(mongo, selective), rulesWith(selective, mongo) })
        {
            rules.TryFindPersistenceFrameProvider(null!, typeof(MongoEntity), out var provider).ShouldBeTrue();
            provider.ShouldBeSameAs(mongo);

            rules.TryFindPersistenceFrameProvider(null!, typeof(PrecedenceSaga), out var sagaProvider).ShouldBeTrue();
            sagaProvider.ShouldBeSameAs(mongo);
        }
    }

    [Fact]
    public void mongodb_sorts_after_every_selective_provider_regardless_of_registration_order()
    {
        var selective1 = new SelectiveProvider(typeof(SelectiveEntity));
        var selective2 = new SelectiveProvider(typeof(MongoEntity));
        var mongo = new MongoDbPersistenceFrameProvider();

        rulesWith(mongo, selective1, selective2).OrderedPersistenceProviders()
            .ShouldBe([selective1, selective2, mongo]);
    }

    // ---- End to end: a real host with MongoDB AND a selective provider, both registration orders ----

    private async Task<IHost> buildMixedHostAsync(bool mongoFirst)
    {
        await _fixture.ClearAll();
        await _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .DropCollectionAsync(MongoConstants.EntityCollectionName(typeof(MongoEntity)));

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(MixedPersistenceHandler))
                    .IncludeType(typeof(PrecedenceSaga));

                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);

                // Mirror how a real selective integration (EF Core) registers: InsertFirst. The two
                // orders below therefore produce BOTH raw list shapes — [Selective, Mongo] and
                // [Mongo, Selective] — and the resolution must be identical for each.
                if (mongoFirst)
                {
                    opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                    opts.CodeGeneration.InsertFirstPersistenceStrategy<SelectiveEntityProvider>();
                }
                else
                {
                    opts.CodeGeneration.InsertFirstPersistenceStrategy<SelectiveEntityProvider>();
                    opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                }
            }).StartAsync();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task mixed_persistence_routes_each_entity_to_its_own_provider(bool mongoFirst)
    {
        using var host = await buildMixedHostAsync(mongoFirst);

        var mongoId = Guid.NewGuid().ToString();
        await _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<MongoEntity>(MongoConstants.EntityCollectionName(typeof(MongoEntity)))
            .InsertOneAsync(new MongoEntity { Id = mongoId, Name = "from-mongo" });

        var selectiveId = Guid.NewGuid().ToString();
        var bus = host.Services.GetRequiredService<IMessageBus>();

        await bus.InvokeAsync(new LoadSelective(selectiveId));
        await bus.InvokeAsync(new LoadMongo(mongoId));

        // The selective provider's own load frame must have supplied the entity. When MongoDB wins the
        // selection instead, its [Entity] load reads the (empty) `selectiveentity` collection and the
        // handler observes null.
        MixedPersistenceHandler.Loaded[selectiveId].ShouldBe("from-fake");
        MixedPersistenceHandler.Loaded[mongoId].ShouldBe("from-mongo");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task sagas_still_resolve_to_mongodb_with_a_selective_provider_present(bool mongoFirst)
    {
        using var host = await buildMixedHostAsync(mongoFirst);

        var sagaId = Guid.NewGuid();
        await host.Services.GetRequiredService<IMessageBus>().InvokeAsync(new StartPrecedence(sagaId));

        var saga = await _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<PrecedenceSaga>(MongoConstants.SagaCollectionName(typeof(PrecedenceSaga)))
            .Find(x => x.Id == sagaId)
            .FirstOrDefaultAsync();

        saga.ShouldNotBeNull();
        saga.Started.ShouldBeTrue();
    }

    // ---- Fixtures ----

    public class SelectiveEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public class MongoEntity
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
    }

    public record LoadSelective(string Id);
    public record LoadMongo(string Id);
    public record StartPrecedence(Guid Id);

    public static class MixedPersistenceHandler
    {
        public static readonly ConcurrentDictionary<string, string?> Loaded = new();

        public static void Handle(LoadSelective cmd, [Entity(Required = false)] SelectiveEntity? entity)
            => Loaded[cmd.Id] = entity?.Name;

        public static void Handle(LoadMongo cmd, [Entity(Required = false)] MongoEntity? entity)
            => Loaded[cmd.Id] = entity?.Name;
    }

    public class PrecedenceSaga : Saga
    {
        public Guid Id { get; set; }
        public bool Started { get; set; }

        public void Start(StartPrecedence cmd)
        {
            Id = cmd.Id;
            Started = true;
        }
    }

    /// <summary>The "database" behind the selective provider: every id resolves to a known entity.</summary>
    public static class SelectiveEntityStore
    {
        public static SelectiveEntity Load(string id) => new() { Id = id, Name = "from-fake" };
    }

    /// <summary>
    /// A selective provider in the EF Core mould: claims exactly one entity type and supplies a real
    /// load frame for it, declines everything else, and is not a catch-all.
    /// </summary>
    public class SelectiveEntityProvider : SelectiveProvider
    {
        public SelectiveEntityProvider() : base(typeof(SelectiveEntity)) { }

        public override Type DetermineSagaIdType(Type sagaType, IServiceContainer container) => typeof(string);

        public override Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
        {
            var call = new MethodCall(typeof(SelectiveEntityStore), nameof(SelectiveEntityStore.Load));
            call.Arguments[0] = sagaId;
            return call;
        }
    }

    public class SelectiveProvider : IPersistenceFrameProvider
    {
        private readonly Type _entityType;
        public SelectiveProvider(Type entityType) => _entityType = entityType;

        public bool IsCatchAll => false;

        public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
        {
            persistenceService = GetType();
            return entityType == _entityType;
        }

        public void ApplyTransactionSupport(IChain chain, IServiceContainer container) { }
        public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType) { }
        public bool CanApply(IChain chain, IServiceContainer container) => false;

        public virtual Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
            => throw new NotSupportedException();

        public virtual Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
            => throw new NotSupportedException();

        public Frame DetermineInsertFrame(Variable saga, IServiceContainer container) => throw new NotSupportedException();
        public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container) => throw new NotSupportedException();
        public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container) => throw new NotSupportedException();
        public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container) => throw new NotSupportedException();
        public Frame DetermineStoreFrame(Variable saga, IServiceContainer container) => throw new NotSupportedException();
        public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container) => throw new NotSupportedException();
        public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container) => throw new NotSupportedException();
        public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity) => [];
    }
}
