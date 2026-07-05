using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.Descriptors;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// MongoDB-backed implementation of <see cref="ISagaStoreDiagnostics"/>. Walks the Wolverine
/// handler graph for every saga state type the <see cref="MongoDbPersistenceFrameProvider"/> can
/// persist, then routes <c>ReadSaga</c> / <c>ListSagaInstances</c> calls through the configured
/// database so monitoring tools (CritterWatch in particular) can surface saga state JSON without
/// reaching into MongoDB directly. Mirrors <c>RavenDbSagaStoreDiagnostics</c>; MongoDB stores each
/// saga type in its own <c>wolverine_saga_&lt;type&gt;</c> collection with a native <c>_id</c>
/// (Guid/string/int/long), so — unlike RavenDb's string-only ids — the identity is used as-is.
/// </summary>
/// <remarks>
/// <para>Wolverine's runtime aggregator (<c>AggregateSagaStoreDiagnostics</c>) fans out across every
/// registered <see cref="ISagaStoreDiagnostics"/>, so this participates alongside Marten / EF Core /
/// RavenDb when a host wires more than one saga storage. Registered in
/// <see cref="WolverineMongoDbExtensions.UseMongoDbPersistence"/>.</para>
///
/// <para><b>Reflective bridge over Wolverine internals.</b> Every in-repo saga-diagnostics provider
/// (RavenDb / Marten / EF Core / RDBMS) reaches three Wolverine core members directly —
/// <c>WolverineOptions.HandlerGraph</c>, <c>HandlerGraph.Container</c>, and
/// <c>SagaDescriptorBuilder.Build</c> — because each of those packages is on Wolverine's
/// <c>[InternalsVisibleTo]</c> list. All three members are <c>internal</c>. This package ships
/// OUTSIDE the Wolverine repo (it references the WolverineFx NuGet package), so it reaches the SAME
/// members reflectively. The reflection is isolated in the three <c>Resolve*</c> methods below and is
/// resolved once, non-throwing (a null accessor never breaks DI registration or host startup — the
/// null case is only surfaced if a diagnostic method is actually invoked). <b>TODO(upstream):</b> if
/// this provider is contributed into the Wolverine repo (and added to Wolverine's
/// <c>[InternalsVisibleTo]</c> alongside RavenDb), delete the <c>Resolve*</c> reflection and replace
/// each call site with the direct member access every sibling provider uses.</para>
/// </remarks>
internal sealed class MongoDbSagaStoreDiagnostics : ISagaStoreDiagnostics
{
    private readonly IWolverineRuntime _runtime;
    private readonly IMongoClient _client;
    private readonly string _databaseName;
    private readonly object _gate = new();
    private Dictionary<string, Type>? _sagaIndex;

    // Reflective bridges over Wolverine core internals — see class remarks / TODO(upstream). Resolved
    // once, null on miss (never throwing) so type initialisation and host startup stay safe.
    private static readonly PropertyInfo? _handlerGraphAccessor = ResolveHandlerGraphAccessor();
    private static readonly PropertyInfo? _containerAccessor = ResolveContainerAccessor();
    private static readonly MethodInfo? _buildDescriptor = ResolveBuildDescriptor();

    public MongoDbSagaStoreDiagnostics(IWolverineRuntime runtime, IMongoClient client, string databaseName)
    {
        _runtime = runtime;
        _client = client;
        _databaseName = databaseName;
    }

    public Task<IReadOnlyList<SagaDescriptor>> GetRegisteredSagasAsync(CancellationToken ct)
    {
        var distinct = sagaIndex().Values.Distinct().ToArray();
        var descriptors = distinct.Select(buildDescriptor).ToArray();
        return Task.FromResult<IReadOnlyList<SagaDescriptor>>(descriptors);
    }

    // A private generic helper readSagaAsync<TSaga> is invoked reflectively because the saga type is
    // only known at runtime; MongoDB needs the closed IMongoCollection<TSaga> to deserialize the saga
    // POCO. Same MakeGenericMethod pattern the provider uses for DetermineStorageActionFrame.
    [UnconditionalSuppressMessage("Trimming", "IL2060",
        Justification = "Generic readSagaAsync<TSaga> invoked reflectively; saga types statically rooted via handler discovery. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericMethod over the runtime saga type; saga types statically rooted via handler discovery. See AOT guide.")]
    public async Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType)) return null;
        if (identity is null) return null;

        var helper = typeof(MongoDbSagaStoreDiagnostics)
            .GetMethod(nameof(readSagaAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType);

        var task = (Task<SagaInstanceState?>)helper.Invoke(this, [sagaType, identity, ct])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<SagaInstanceState?> readSagaAsync<TSaga>(Type sagaType, object identity, CancellationToken ct)
        where TSaga : class
    {
        var collection = _client.GetDatabase(_databaseName)
            .GetCollection<TSaga>(MongoConstants.SagaCollectionName(sagaType));

        // Builders<TSaga>.Filter.Eq("_id", identity): the driver resolves "_id" to the mapped id
        // member and serialises the (boxed, native-typed) identity with that member's serializer —
        // no .ToString() coercion, unlike RavenDb which stores all saga ids as strings.
        var saga = await collection
            .Find(Builders<TSaga>.Filter.Eq("_id", identity))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (saga is null) return null;
        return buildInstance(sagaType, identity, saga);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2060",
        Justification = "Generic querySagasAsync<TSaga> invoked reflectively; saga types statically rooted via handler discovery. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericMethod over the runtime saga type; saga types statically rooted via handler discovery. See AOT guide.")]
    public async Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count, CancellationToken ct)
    {
        if (!sagaIndex().TryGetValue(sagaTypeName, out var sagaType))
            return Array.Empty<SagaInstanceState>();

        var clamped = count <= 0 ? 0 : Math.Min(count, 1000);
        if (clamped == 0) return Array.Empty<SagaInstanceState>();

        var helper = typeof(MongoDbSagaStoreDiagnostics)
            .GetMethod(nameof(querySagasAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(sagaType);

        var task = (Task<IReadOnlyList<SagaInstanceState>>)helper.Invoke(this, [sagaType, clamped, ct])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SagaInstanceState>> querySagasAsync<TSaga>(Type sagaType, int count, CancellationToken ct)
        where TSaga : class
    {
        var collection = _client.GetDatabase(_databaseName)
            .GetCollection<TSaga>(MongoConstants.SagaCollectionName(sagaType));

        var sagas = await collection
            .Find(FilterDefinition<TSaga>.Empty)
            .Limit(count)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var list = new List<SagaInstanceState>(sagas.Count);
        foreach (var saga in sagas)
        {
            // Identity extraction via the driver class map — the same idiomatic mechanism
            // MongoEntityOperations.IdOf uses for entity frames (honours the default Id convention,
            // works for every native id type with no per-type code).
            var id = BsonClassMap.LookupClassMap(sagaType).IdMemberMap?.Getter(saga) ?? string.Empty;
            list.Add(buildInstance(sagaType, id, saga));
        }

        return list;
    }

    // Mirrors RavenDb/Marten/EF Core/RDBMS, all of which call the internal SagaDescriptorBuilder.Build
    // directly. See class remarks / TODO(upstream) — upstream this whole body becomes the one-liner
    // every sibling provider uses:
    //     return SagaDescriptorBuilder.Build(_runtime.Options.HandlerGraph, sagaType, "MongoDb");
    private SagaDescriptor buildDescriptor(Type sagaType)
    {
        if (_buildDescriptor is null)
        {
            throw new InvalidOperationException(
                "Could not resolve Wolverine's internal SagaDescriptorBuilder.Build(HandlerGraph, Type, string?). " +
                "This indicates an incompatible WolverineFx version — keep the external/wolverine submodule pinned " +
                "in sync with the WolverineFx package version in Directory.Packages.props.");
        }

        return (SagaDescriptor)_buildDescriptor.Invoke(null, [handlerGraph(), sagaType, "MongoDb"])!;
    }

    // JsonSerializer.SerializeToElement(saga, sagaType) over a runtime-resolved saga type — identical
    // to RavenDbSagaStoreDiagnostics.buildInstance. IsCompleted reads the base Saga flag.
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Reflection-based STJ over runtime saga type; AOT consumers supply a JsonSerializerContext for their saga types. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Reflection-based STJ over runtime saga type; AOT consumers supply a JsonSerializerContext for their saga types. See AOT guide.")]
    private static SagaInstanceState buildInstance(Type sagaType, object identity, object saga)
    {
        var stateJson = JsonSerializer.SerializeToElement(saga, sagaType);
        var isCompleted = saga is global::Wolverine.Saga sagaBase && sagaBase.IsCompleted();
        return new SagaInstanceState(
            sagaType.FullNameInCode(),
            identity,
            isCompleted,
            stateJson,
            null);
    }

    // Locates the MongoDb-owned saga types via provider.CanPersist (same filter as RavenDb), indexed by
    // both FullName (canonical) and Name (short, caller-friendly) so the aggregator routes on either
    // form. Double-checked lock; built once on first diagnostic call.
    private Dictionary<string, Type> sagaIndex()
    {
        if (_sagaIndex is not null) return _sagaIndex;
        lock (_gate)
        {
            if (_sagaIndex is not null) return _sagaIndex;

            var index = new Dictionary<string, Type>(StringComparer.Ordinal);
            var providers = _runtime.Options.CodeGeneration.PersistenceProviders();
            var mongo = providers.OfType<MongoDbPersistenceFrameProvider>().FirstOrDefault();
            if (mongo is null)
            {
                _sagaIndex = index;
                return index;
            }

            var graph = handlerGraph();
            var container = readContainer(graph);
            var sagaTypes = graph.Chains
                .OfType<SagaChain>()
                .Select(c => c.SagaType)
                .Distinct();

            foreach (var sagaType in sagaTypes)
            {
                bool canPersist;
                try
                {
                    // MongoDbPersistenceFrameProvider.CanPersist is container-agnostic (unconditional
                    // true, Tier 1), so a null container here is harmless — the `!` only silences the
                    // nullable-arg warning for the reflected accessor's may-be-null return.
                    canPersist = mongo.CanPersist(sagaType, container!, out _);
                }
                catch
                {
                    canPersist = false;
                }

                if (!canPersist) continue;
                index.TryAdd(sagaType.FullName!, sagaType);
                index.TryAdd(sagaType.Name, sagaType);
            }

            _sagaIndex = index;
            return index;
        }
    }

    // Reflective accessor for the internal WolverineOptions.HandlerGraph instance (see class remarks).
    private HandlerGraph handlerGraph()
    {
        if (_handlerGraphAccessor?.GetValue(_runtime.Options) is HandlerGraph graph)
        {
            return graph;
        }

        throw new InvalidOperationException(
            "Could not resolve Wolverine's internal WolverineOptions.HandlerGraph. This indicates an " +
            "incompatible WolverineFx version — keep the external/wolverine submodule pinned in sync with " +
            "the WolverineFx package version in Directory.Packages.props.");
    }

    // Reflective accessor for the internal HandlerGraph.Container (an IServiceContainer). May be null if
    // reflection fails; callers treat null defensively (CanPersist is container-agnostic here).
    private static IServiceContainer? readContainer(HandlerGraph graph)
        => _containerAccessor?.GetValue(graph) as IServiceContainer;

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "GetProperty on the well-known public WolverineOptions type; the internal HandlerGraph accessor is present by construction. See AOT guide.")]
    private static PropertyInfo? ResolveHandlerGraphAccessor()
        => typeof(WolverineOptions).GetProperty("HandlerGraph",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "GetProperty on the well-known public HandlerGraph type; the internal Container accessor is present by construction. See AOT guide.")]
    private static PropertyInfo? ResolveContainerAccessor()
        => typeof(HandlerGraph).GetProperty("Container",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    // typeof(SagaChain).Assembly is the Wolverine core assembly (same one that owns SagaDescriptorBuilder).
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Resolving an internal core helper by name during diagnostics; core saga types are statically rooted via handler discovery. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2057",
        Justification = "Assembly.GetType over a constant, in-assembly core type name present in the referenced WolverineFx assembly by construction. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "GetMethod on the resolved internal SagaDescriptorBuilder type; the method is present by construction. See AOT guide.")]
    private static MethodInfo? ResolveBuildDescriptor()
        => typeof(SagaChain).Assembly
            .GetType("Wolverine.Persistence.Sagas.SagaDescriptorBuilder")
            ?.GetMethod("Build", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
}
