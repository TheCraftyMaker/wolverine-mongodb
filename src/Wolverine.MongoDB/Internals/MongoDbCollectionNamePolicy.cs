using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// Eagerly claims a MongoDB collection for every saga and entity type this provider will persist, so a
/// collection-name collision (see <see cref="MongoCollectionNaming"/>) fails the host's start instead of
/// silently mixing two types' documents.
///
/// <para><b>Why an <see cref="IHandlerPolicy"/> and not an <c>IHostedService</c>.</b>
/// <c>WolverineRuntime.StartAsync</c> calls <c>Handlers.Compile(Options, _container)</c> before it starts
/// any messaging transport, and <c>HandlerGraph.Compile</c> runs every registered handler policy
/// (<c>foreach (var policy in handlerPolicies(options)) policy.Apply(allChains, Rules, container);</c>).
/// So this runs before a single listener is live — earlier than an <c>IHostedService</c> validator, which
/// only runs after the runtime's own <c>StartAsync</c> has already brought transports up. <c>Compile</c>
/// is also unconditional: it runs in <c>TypeLoadMode.Static</c> too (Static only changes how handler
/// types are <i>discovered</i> and skips frame construction), which is exactly the gap that forces
/// <see cref="MongoIdentityMapping"/> to duplicate itself in the runtime collection accessors.</para>
///
/// <para>Detection is deliberately eager rather than lazy for a second reason: a throw from the runtime
/// collection accessors would happen inside an open transaction, mid-handler, so Wolverine's retry/dead
/// letter policy would turn a configuration mistake into a message-delivery incident. The accessors still
/// claim (defence in depth for any shape this walk misses), but the operator's deploy is what fails.</para>
///
/// <para><b>What it can't see.</b> Collections the application itself owns — an
/// <c>IMongoDatabase.GetCollection&lt;T&gt;("orders")</c> in a repository — are not in the handler graph
/// and cannot be checked here. The explicit mapping API is the remedy for that overlap.</para>
/// </summary>
internal sealed class MongoDbCollectionNamePolicy : IHandlerPolicy
{
    private readonly string _databaseName;

    internal MongoDbCollectionNamePolicy(string databaseName) => _databaseName = databaseName;

    public void Apply(IReadOnlyList<HandlerChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            if (chain is SagaChain sagaChain
                && rules.GetPersistenceProviders(chain, container) is MongoDbPersistenceFrameProvider)
            {
                MongoCollectionNaming.ClaimSaga(_databaseName, sagaChain.SagaType);
            }

            foreach (var persistedType in persistedTypes(chain))
            {
                if (!rules.TryFindPersistenceFrameProvider(container, persistedType, out var provider)
                    || provider is not MongoDbPersistenceFrameProvider)
                {
                    continue;
                }

                // Mirrors the frame factories' own saga-vs-entity branch
                // (MongoDbPersistenceFrameProvider: variable.VariableType.CanBeCastTo<Saga>()). A saga
                // reaching the generic write paths is rejected outright by those factories (LD4), so only
                // the [Entity] load can legitimately hand us a Saga here.
                if (persistedType.CanBeCastTo<Saga>())
                {
                    MongoCollectionNaming.ClaimSaga(_databaseName, persistedType);
                }
                else
                {
                    MongoCollectionNaming.ClaimEntity(_databaseName, persistedType);
                }
            }
        }
    }

    /// <summary>
    /// The types Wolverine will ask this provider to persist for one chain, mirroring exactly the three
    /// shapes core keys on and nothing else:
    /// <list type="number">
    /// <item><description>a return value closing <c>IStorageAction&lt;T&gt;</c> — <c>Insert&lt;T&gt;</c>,
    /// <c>Update&lt;T&gt;</c>, <c>Store&lt;T&gt;</c>, <c>Delete&lt;T&gt;</c>, <c>Nothing&lt;T&gt;</c>, or the
    /// raw interface (<c>Storage.TryApply</c>, <c>IStorageAction&lt;T&gt;.BuildFrame</c>);</description></item>
    /// <item><description>a <c>UnitOfWork&lt;T&gt;</c> return — note it derives from
    /// <c>List&lt;IStorageAction&lt;T&gt;&gt;</c> and does <i>not</i> itself close
    /// <c>IStorageAction&lt;&gt;</c>, so shape 1 misses it;</description></item>
    /// <item><description>a handler parameter carrying <c>[Entity]</c>
    /// (<c>EntityAttribute.Modify</c>).</description></item>
    /// </list>
    /// A shape this walk misses degrades to the claim the runtime collection accessors take, never to
    /// silence.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "GetInterfaces()/GetParameters() over handler return and parameter types that handler discovery has already statically rooted; no assembly scanning is introduced. See AOT guide.")]
    private static IEnumerable<Type> persistedTypes(HandlerChain chain)
    {
        foreach (var call in chain.HandlerCalls())
        {
            foreach (var created in call.Creates)
            {
                foreach (var entityType in storageActionTypes(created))
                {
                    yield return entityType;
                }
            }

            foreach (var parameter in call.Method.GetParameters())
            {
                if (parameter.GetCustomAttributes(true).OfType<EntityAttribute>().Any())
                {
                    yield return parameter.ParameterType;
                }
            }
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "GetInterfaces() over a handler return type already statically rooted by handler discovery. See AOT guide.")]
    private static IEnumerable<Type> storageActionTypes(Variable created)
    {
        var returnType = created.VariableType;

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(IStorageAction<>))
        {
            yield return returnType.GetGenericArguments()[0];
        }

        foreach (var contract in returnType.GetInterfaces())
        {
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IStorageAction<>))
            {
                yield return contract.GetGenericArguments()[0];
            }
        }

        for (var level = returnType; level != null && level != typeof(object); level = level.BaseType)
        {
            if (level.IsGenericType && level.GetGenericTypeDefinition() == typeof(UnitOfWork<>))
            {
                yield return level.GetGenericArguments()[0];
                break;
            }
        }
    }
}
