using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using MongoDB.Driver;
using Wolverine.Persistence.Sagas;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// Saga read/write operations executed against the active Wolverine-managed MongoDB session.
/// The codegen frames below emit calls into these helpers rather than inlining the driver
/// calls directly: <c>IMongoCollection&lt;T&gt;.Find(...)</c> and <c>FirstOrDefaultAsync(...)</c>
/// are MongoDB.Driver <i>extension</i> methods, and the generated handler does not carry a
/// <c>using MongoDB.Driver;</c>. Centralizing the calls here keeps the generated code free of
/// extension-method/using concerns while guaranteeing every operation runs on the supplied
/// <see cref="IClientSessionHandle"/> — i.e. inside the same transaction as the outbox commit.
/// Mirrors the <c>CosmosDbStorageActionApplier</c> pattern used by Wolverine.CosmosDb.
/// </summary>
public static class MongoSagaOperations
{
    /// <summary>
    /// Load a saga document by its <c>_id</c> within the session/transaction. Returns
    /// <c>null</c> when no document matches (the caller's null-guard becomes the
    /// "unknown saga" / "start new saga" branch).
    /// </summary>
    public static Task<TSaga> LoadSagaAsync<TSaga, TId>(
        IMongoDatabase database, IClientSessionHandle session, TId sagaId, CancellationToken cancellationToken)
        where TSaga : class
    {
        var collection = database.GetCollection<TSaga>(MongoConstants.SagaCollectionName(typeof(TSaga)));
        return collection
            .Find(session, Builders<TSaga>.Filter.Eq("_id", sagaId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Upsert the saga document keyed by <c>_id</c> within the session/transaction. Used for both
    /// the insert (new saga) and update (existing saga) paths in the string-id baseline (S6);
    /// optimistic concurrency (S8) will split these into distinct insert/update operations.
    /// </summary>
    public static Task StoreSagaAsync<TSaga, TId>(
        IMongoDatabase database, IClientSessionHandle session, TSaga saga, TId sagaId, CancellationToken cancellationToken)
        where TSaga : class
    {
        var collection = database.GetCollection<TSaga>(MongoConstants.SagaCollectionName(typeof(TSaga)));
        return collection.ReplaceOneAsync(
            session,
            Builders<TSaga>.Filter.Eq("_id", sagaId),
            saga,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>
    /// Delete the completed saga document by <c>_id</c> within the session/transaction.
    /// Unguarded by version (matches Wolverine's lightweight SQL provider).
    /// </summary>
    public static Task DeleteSagaAsync<TSaga, TId>(
        IMongoDatabase database, IClientSessionHandle session, TId sagaId, CancellationToken cancellationToken)
        where TSaga : class
    {
        var collection = database.GetCollection<TSaga>(MongoConstants.SagaCollectionName(typeof(TSaga)));
        return collection.DeleteOneAsync(
            session,
            Builders<TSaga>.Filter.Eq("_id", sagaId),
            cancellationToken: cancellationToken);
    }
}

/// <summary>
/// Loads the saga document from MongoDB by <c>_id</c> on the Wolverine-managed session.
/// Exposes the loaded saga as its single created <see cref="Variable"/> (<see cref="Saga"/>),
/// which Wolverine's <c>ResolveSagaFrame</c> consumes via <c>loadFrame.Creates.First()</c>.
/// </summary>
internal class LoadSagaFrame : AsyncFrame
{
    private readonly Variable _sagaId;
    private Variable? _cancellation;
    private Variable? _database;
    private Variable? _session;

    public LoadSagaFrame(Type sagaType, Variable sagaId)
    {
        _sagaId = sagaId;
        uses.Add(sagaId);
        Saga = new Variable(sagaType, this);
    }

    public Variable Saga { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IClientSessionHandle));
        yield return _session;

        _database = chain.FindVariable(typeof(IMongoDatabase));
        yield return _database;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.BlankLine();
        writer.WriteComment("Load the saga document by _id on the MongoDB session (null if it does not exist)");
        writer.Write(
            $"{Saga.VariableType.FullNameInCode()} {Saga.Usage} = await {typeof(MongoSagaOperations).FullNameInCode()}.{nameof(MongoSagaOperations.LoadSagaAsync)}<{Saga.VariableType.FullNameInCode()}, {_sagaId.VariableType.FullNameInCode()}>({_database!.Usage}, {_session!.Usage}, {_sagaId.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Upserts the saga document keyed by <c>_id</c> on the Wolverine-managed session. The
/// <c>DetermineInsertFrame</c>/<c>DetermineUpdateFrame</c> factories hand this frame only the
/// <c>saga</c> variable (no <c>sagaId</c>), so the <c>_id</c> filter value is read from the saga's
/// resolved identity member (e.g. <c>saga.Id</c>) — resolved once at frame-build time.
/// </summary>
internal class StoreSagaFrame : AsyncFrame
{
    private readonly Variable _saga;
    private readonly string _idMember;
    private readonly Type _idType;
    private Variable? _cancellation;
    private Variable? _database;
    private Variable? _session;

    public StoreSagaFrame(Variable saga)
    {
        _saga = saga;
        uses.Add(saga);

        var sagaType = saga.VariableType;
        var idMember = SagaChain.DetermineSagaIdMember(sagaType, sagaType);
        _idMember = idMember?.Name ?? "Id";
        _idType = idMember?.GetRawMemberType() ?? typeof(string);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IClientSessionHandle));
        yield return _session;

        _database = chain.FindVariable(typeof(IMongoDatabase));
        yield return _database;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.BlankLine();
        writer.WriteComment("Upsert the saga document by _id on the MongoDB session (inside the outbox transaction)");
        writer.Write(
            $"await {typeof(MongoSagaOperations).FullNameInCode()}.{nameof(MongoSagaOperations.StoreSagaAsync)}<{_saga.VariableType.FullNameInCode()}, {_idType.FullNameInCode()}>({_database!.Usage}, {_session!.Usage}, {_saga.Usage}, {_saga.Usage}.{_idMember}, {_cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Deletes the completed saga document by <c>_id</c> on the Wolverine-managed session. The
/// <c>DetermineDeleteFrame</c> factory hands this frame the <c>sagaId</c> variable directly, so
/// the filter keys off it (not the saga document).
/// </summary>
internal class DeleteSagaFrame : AsyncFrame
{
    private readonly Variable _sagaId;
    private readonly Variable _saga;
    private Variable? _cancellation;
    private Variable? _database;
    private Variable? _session;

    public DeleteSagaFrame(Variable sagaId, Variable saga)
    {
        _sagaId = sagaId;
        _saga = saga;
        uses.Add(sagaId);
        uses.Add(saga);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _session = chain.FindVariable(typeof(IClientSessionHandle));
        yield return _session;

        _database = chain.FindVariable(typeof(IMongoDatabase));
        yield return _database;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.BlankLine();
        writer.WriteComment("Delete the completed saga document by _id on the MongoDB session (inside the outbox transaction)");
        writer.Write(
            $"await {typeof(MongoSagaOperations).FullNameInCode()}.{nameof(MongoSagaOperations.DeleteSagaAsync)}<{_saga.VariableType.FullNameInCode()}, {_sagaId.VariableType.FullNameInCode()}>({_database!.Usage}, {_session!.Usage}, {_sagaId.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}
