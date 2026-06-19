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
    // Saga persistence IS supported (see the saga members below). Generic, non-saga document
    // persistence (the IStorageAction<T> / return-value [Entity] surface) is not.
    private const string GenericPersistenceNotSupported =
        "Generic document persistence is not supported by Wolverine.MongoDB; only saga storage and the outbox transaction are.";

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

        // Saga creation support. Scoped to Saga subclasses — NOT unconditional. Cosmos returns
        // true for everything because it also implements DetermineStorageActionFrame; Mongo's
        // DetermineStorageActionFrame still throws, so advertising generic [Entity]/Insert<T>/
        // Update<T>/IStorageAction<T> persistence would blow up at codegen. Sagas only.
        return entityType.CanBeCastTo<Saga>();
    }

    // String-id baseline (S6). S7 generalizes to the saga's native identity-member type.
    // Only consulted on the envelope-header-only identity path; message-member identity uses
    // the member's own runtime type via PullSagaIdFromMessageFrame.
    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
        => typeof(string);

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
        => new LoadSagaFrame(sagaType, sagaId);

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
        => new StoreSagaFrame(saga);

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
        => new CommitMongoTransactionFrame();

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
        => new StoreSagaFrame(saga);

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
        => new DeleteSagaFrame(sagaId, saga);

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
        => DetermineUpdateFrame(saga, container);

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    {
        throw new NotSupportedException(GenericPersistenceNotSupported);
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        throw new NotSupportedException(GenericPersistenceNotSupported);
    }

    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity)
    {
        return [];
    }
}
