using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using MongoDB.Driver;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;

namespace Wolverine.MongoDB.Internals;

public class MongoDbPersistenceFrameProvider : IPersistenceFrameProvider
{
    public void ApplyTransactionSupport(IChain chain, IServiceContainer container)
    {
        if (!chain.Middleware.OfType<TransactionalFrame>().Any())
        {
            // TransactionalFrame opens the session/transaction and wraps the chain in a
            // try/catch. It is marked IFlushesMessages so Wolverine does not append a
            // standalone FlushOutgoingMessages postprocessor; the commit postprocessor
            // commits the transaction and then flushes — committing AFTER the handler
            // writes and flushing AFTER the commit (avoiding flush-before-commit).
            chain.Middleware.Add(new TransactionalFrame(chain));

            // Saga chains receive their single commit+flush from CommitUnitOfWorkFrame (which
            // SagaChain inlines right after the saga write), so the postprocessor must be
            // skipped for them — otherwise CommitMongoTransactionFrame is emitted twice and the
            // outbox is flushed twice. Mirrors the Cosmos/RavenDb `chain is not SagaChain` guard.
            if (chain is not SagaChain)
            {
                chain.Postprocessors.Add(new CommitMongoTransactionFrame());
            }
        }
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
    {
        ApplyTransactionSupport(chain, container);
    }

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        // Saga chains are persisted by this provider's saga frames. Without this, the saga
        // chain would not select this provider and the saga members below would never run.
        if (chain is SagaChain)
        {
            return true;
        }

        // Handlers that declare the session (or the unit-of-work helper, Task 9) as a
        // method parameter explicitly opt into the transaction.
        if (chain.HandlerCalls().Any(call => call.Method.GetParameters().Any(p =>
                p.ParameterType == typeof(IClientSessionHandle)
                || p.ParameterType == typeof(MongoDbUnitOfWork))))
        {
            return true;
        }

        var serviceDependencies = chain
            .ServiceDependencies(container, new[] { typeof(IMongoDatabase), typeof(IMongoClient) })
            .ToArray();

        return serviceDependencies.Any(x =>
            x == typeof(IMongoDatabase)
            || x == typeof(IMongoClient)
            || (x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IMongoCollection<>)));
    }

    public bool CanPersist(Type entityType, IServiceContainer container, out Type persistenceService)
    {
        persistenceService = typeof(IMongoDatabase);

        // Tier 1: DetermineStorageActionFrame + DetermineDeleteFrame(Variable,…) are now implemented,
        // so we advertise generic persistence like Cosmos (CosmosDbPersistenceFrameProvider:51-55) and
        // RavenDb (RavenDbPersistenceFrameProvider:56-60). The saga-vs-entity distinction is handled in
        // the frame factories below (branch on CanBeCastTo<Saga>()), NOT here. Unconditional true is
        // required for [Entity] loads, which select the provider via CanPersist(parameterType).
        return true;
    }

    // Resolve the saga's native identity-member type (Guid/int/long/string) via Wolverine's own
    // resolver — the same call the built-in lightweight provider uses
    // (LightweightSagaPersistenceFrameProvider.cs:80-82). No hand-rolled reflection.
    //
    // Scope: core only consults this on the envelope-header-only identity path
    // (SagaChain.cs:290-292, the SagaIdMember == null branch). Messages that carry a saga-id
    // member resolve the type from that member directly via PullSagaIdFromMessageFrame, so the
    // load/delete frames key _id off whatever typed sagaId variable they are handed regardless.
    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
        => SagaChain.DetermineSagaIdMember(sagaType, sagaType)?.GetRawMemberType()
           ?? throw new ArgumentException(
               $"Unable to determine the identity member for {sagaType.FullNameInCode()}", nameof(sagaType));

    // Used for sagas AND [Entity] parameter loads (EntityAttribute.cs:165). Branch saga-vs-entity:
    // Saga subclasses keep the version-aware saga load; plain entities get the simple entity load.
    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
        => sagaType.CanBeCastTo<Saga>()
            ? new LoadSagaFrame(sagaType, sagaId)
            : new LoadEntityFrame(sagaType, sagaId);

    // Insert and update DIVERGE (S8 optimistic concurrency): insert is unguarded and stamps the
    // initial Saga.Version; update is version-guarded and throws SagaConcurrencyException on a
    // stale write. SagaChain emits insert only on the saga-does-not-exist branch and update only
    // on the saga-exists branch, so the two never collide. The completion DELETE is deliberately
    // left UNGUARDED by version (matches the lightweight SQL provider's DatabaseSagaSchema.cs) —
    // completion is terminal and a version throw there would only obstruct cleanup.
    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
        => saga.VariableType.CanBeCastTo<Saga>()
            ? new InsertSagaFrame(saga)
            : new MongoUpsertEntityFrame(saga);

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
        => new CommitMongoTransactionFrame();

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
        => saga.VariableType.CanBeCastTo<Saga>()
            ? new UpdateSagaFrame(saga)
            : new MongoUpsertEntityFrame(saga);

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
        => new DeleteSagaFrame(sagaId, saga);

    // For sagas this is inert: SagaChain never calls DetermineStoreFrame (it emits explicit
    // Insert/Update/Delete, SagaChain.cs:395,423-424); the saga branch is retained only to satisfy the
    // interface and route consistently to the version-guarded update. The live Store<T> path is the
    // entity branch → MongoUpsertEntityFrame.
    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
        => saga.VariableType.CanBeCastTo<Saga>()
            ? DetermineUpdateFrame(saga, container)
            : new MongoUpsertEntityFrame(saga);

    // Generic single-variable delete (Delete<T> return value, Delete.cs:26). Entity-only — sagas reach
    // delete through the two-variable overload above via SagaChain. The handed variable IS the entity
    // (core's EntityVariable); the _id is class-map-extracted inside MongoEntityOperations.DeleteAsync.
    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
        => new MongoDeleteEntityByVariableFrame(variable);

    // Generic IStorageAction<T>/UnitOfWork<T> return value (IStorageAction.cs:27,:96). Entity-only.
    // Mirrors RavenDb/Cosmos's MethodCall-to-a-static-applier pattern: codegen auto-resolves
    // IMongoDatabase (0), IClientSessionHandle (1) and CancellationToken (3) from the chain's
    // variables; we set only Arguments[2] = action. The applier runs on the TransactionalFrame session
    // (ApplyTransactionSupport is invoked before this frame), so the entity write commits atomically
    // with the outbox.
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "MakeGenericMethod over the runtime entity type during Dynamic codegen; matches RavenDb/Cosmos providers. AOT consumers run pre-generated frames in TypeLoadMode.Static.")]
    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        var method = typeof(MongoEntityOperations)
            .GetMethod(nameof(MongoEntityOperations.ApplyStorageActionAsync))!
            .MakeGenericMethod(entityType);

        var call = new MethodCall(typeof(MongoEntityOperations), method);
        call.Arguments[2] = action; // (IMongoDatabase, IClientSessionHandle, IStorageAction<T> action, CancellationToken)
        return call;
    }

    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity)
    {
        return [];
    }
}
