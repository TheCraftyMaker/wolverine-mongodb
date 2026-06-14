using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using MongoDB.Driver;
using Wolverine.Configuration;
using Wolverine.Persistence;

namespace Wolverine.MongoDB.Internals;

public class MongoDbPersistenceFrameProvider : IPersistenceFrameProvider
{
    private const string SagaNotSupported =
        "Saga storage is not yet supported by Wolverine.MongoDB";

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
            chain.Postprocessors.Add(new CommitMongoTransactionFrame());
        }
    }

    public void ApplyTransactionSupport(IChain chain, IServiceContainer container, Type entityType)
    {
        ApplyTransactionSupport(chain, container);
    }

    public bool CanApply(IChain chain, IServiceContainer container)
    {
        // Handlers that declare the session (or the unit-of-work helper, Task 9) as a
        // method parameter explicitly opt into the transaction.
        if (chain.HandlerCalls().Any(call => call.Method.GetParameters().Any(p =>
                p.ParameterType == typeof(IClientSessionHandle))))
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

        // Saga storage is deferred; only outbox/transaction support is offered.
        return false;
    }

    public Type DetermineSagaIdType(Type sagaType, IServiceContainer container)
    {
        throw new NotSupportedException(SagaNotSupported);
    }

    public Frame DetermineLoadFrame(IServiceContainer container, Type sagaType, Variable sagaId)
    {
        throw new NotSupportedException(SagaNotSupported);
    }

    public Frame DetermineInsertFrame(Variable saga, IServiceContainer container)
    {
        throw new NotSupportedException(SagaNotSupported);
    }

    public Frame CommitUnitOfWorkFrame(Variable saga, IServiceContainer container)
    {
        throw new NotSupportedException(SagaNotSupported);
    }

    public Frame DetermineUpdateFrame(Variable saga, IServiceContainer container)
    {
        throw new NotSupportedException(SagaNotSupported);
    }

    public Frame DetermineDeleteFrame(Variable sagaId, Variable saga, IServiceContainer container)
    {
        throw new NotSupportedException(SagaNotSupported);
    }

    public Frame DetermineStoreFrame(Variable saga, IServiceContainer container)
    {
        throw new NotSupportedException(SagaNotSupported);
    }

    public Frame DetermineDeleteFrame(Variable variable, IServiceContainer container)
    {
        throw new NotSupportedException(SagaNotSupported);
    }

    public Frame DetermineStorageActionFrame(Type entityType, Variable action, IServiceContainer container)
    {
        throw new NotSupportedException(SagaNotSupported);
    }

    public Frame[] DetermineFrameToNullOutMaybeSoftDeleted(Variable entity)
    {
        return [];
    }
}
