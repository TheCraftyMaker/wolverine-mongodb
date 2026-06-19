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
///
/// Insert and update intentionally diverge (S8): insert is unguarded and stamps the initial
/// <see cref="Saga.Version"/>; update is optimistic-concurrency guarded on the loaded version and
/// throws <see cref="SagaConcurrencyException"/> on a conflict. This mirrors Wolverine's own
/// lightweight SQL provider (<c>DatabaseSagaSchema</c>) rather than the string-only,
/// last-write-wins Cosmos/RavenDb providers.
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
    /// Insert a brand-new saga document within the session/transaction, stamping the initial
    /// <see cref="Saga.Version"/> (1). Deliberately <b>unguarded</b> by version: a new saga has no
    /// prior version to guard against. The collection's implicit unique <c>_id</c> index is the
    /// only guard — a concurrent double-start of the same id fails the second insert with a
    /// duplicate-key error, aborting that transaction so the message retries (and, on retry, loads
    /// the now-existing saga onto the version-guarded update path) rather than silently clobbering
    /// the first start. Mirrors <c>DatabaseSagaSchema.InsertAsync</c> in the lightweight SQL provider.
    /// </summary>
    public static Task InsertSagaAsync<TSaga>(
        IMongoDatabase database, IClientSessionHandle session, TSaga saga, CancellationToken cancellationToken)
        where TSaga : Saga
    {
        var collection = database.GetCollection<TSaga>(MongoConstants.SagaCollectionName(typeof(TSaga)));
        saga.Version = 1;
        return collection.InsertOneAsync(session, saga, options: null, cancellationToken);
    }

    /// <summary>
    /// Optimistic-concurrency-guarded update of an existing saga document within the
    /// session/transaction. Captures the version the saga was loaded at (<c>oldVersion</c>),
    /// advances the in-memory saga to <c>oldVersion + 1</c>, then replaces only the document still
    /// at <c>(_id, oldVersion)</c>. The new version must be written into the document <i>before</i>
    /// the replace because MongoDB stores the saga POCO directly (unlike the RDBMS providers, which
    /// keep the version in a separate column and bump it in SQL).
    ///
    /// <para>Throws <see cref="SagaConcurrencyException"/> when no document matched — i.e. a
    /// competing writer already advanced the version, or the saga was deleted. The thrown exception
    /// propagates out of the generated handler before the commit frame runs, so the
    /// <see cref="TransactionalFrame"/> rolls back the saga write <i>and</i> the outbox together.</para>
    /// </summary>
    public static async Task UpdateSagaAsync<TSaga, TId>(
        IMongoDatabase database, IClientSessionHandle session, TSaga saga, TId sagaId, CancellationToken cancellationToken)
        where TSaga : Saga
    {
        var collection = database.GetCollection<TSaga>(MongoConstants.SagaCollectionName(typeof(TSaga)));

        var oldVersion = saga.Version;
        saga.Version = oldVersion + 1;

        var filter = Builders<TSaga>.Filter.And(
            Builders<TSaga>.Filter.Eq("_id", sagaId),
            Builders<TSaga>.Filter.Eq(x => x.Version, oldVersion));

        var result = await collection
            .ReplaceOneAsync(session, filter, saga, new ReplaceOptions { IsUpsert = false }, cancellationToken)
            .ConfigureAwait(false);

        // ModifiedCount == 0 means no document was at (_id, oldVersion). ModifiedCount (rather than
        // MatchedCount) is correct here precisely because the replacement always changes the Version
        // field, so a matched document is always modified — there is no "matched-but-unchanged" case
        // that would make the two counts diverge.
        if (result.ModifiedCount == 0)
        {
            throw new SagaConcurrencyException(
                $"Saga of type {typeof(TSaga).FullNameInCode()} and id {sagaId} cannot be updated because of optimistic concurrency violations");
        }
    }

    /// <summary>
    /// Delete the completed saga document by <c>_id</c> within the session/transaction.
    /// Deliberately <b>unguarded</b> by version (matches Wolverine's lightweight SQL provider,
    /// <c>DatabaseSagaSchema.DeleteAsync</c>): completion is terminal, so a stale-version delete
    /// still correctly removes the saga and a concurrency throw here would only obstruct cleanup.
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
/// Inserts a new saga document on the Wolverine-managed session and stamps its initial
/// <see cref="Saga.Version"/>. Emitted (conditionally on <c>!IsCompleted()</c>) by
/// <c>SagaChain</c> on the saga-does-not-exist branch. Unguarded — see
/// <see cref="MongoSagaOperations.InsertSagaAsync{TSaga}"/>.
/// </summary>
internal class InsertSagaFrame : AsyncFrame
{
    private readonly Variable _saga;
    private Variable? _cancellation;
    private Variable? _database;
    private Variable? _session;

    public InsertSagaFrame(Variable saga)
    {
        _saga = saga;
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
        writer.WriteComment("Insert the new saga document on the MongoDB session (inside the outbox transaction), stamping the initial version");
        writer.Write(
            $"await {typeof(MongoSagaOperations).FullNameInCode()}.{nameof(MongoSagaOperations.InsertSagaAsync)}<{_saga.VariableType.FullNameInCode()}>({_database!.Usage}, {_session!.Usage}, {_saga.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Version-guarded update of an existing saga document on the Wolverine-managed session. The
/// <c>DetermineUpdateFrame</c> factory hands this frame only the <c>saga</c> variable (no
/// <c>sagaId</c>), so the <c>_id</c> filter value is read from the saga's resolved identity member
/// (e.g. <c>saga.Id</c>) — resolved once at frame-build time. The version guard +
/// <see cref="SagaConcurrencyException"/> live in
/// <see cref="MongoSagaOperations.UpdateSagaAsync{TSaga,TId}"/>.
/// </summary>
internal class UpdateSagaFrame : AsyncFrame
{
    private readonly Variable _saga;
    private readonly string _idMember;
    private readonly Type _idType;
    private Variable? _cancellation;
    private Variable? _database;
    private Variable? _session;

    public UpdateSagaFrame(Variable saga)
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
        writer.WriteComment("Version-guarded update of the saga document by _id on the MongoDB session (inside the outbox transaction); throws SagaConcurrencyException on a stale version");
        writer.Write(
            $"await {typeof(MongoSagaOperations).FullNameInCode()}.{nameof(MongoSagaOperations.UpdateSagaAsync)}<{_saga.VariableType.FullNameInCode()}, {_idType.FullNameInCode()}>({_database!.Usage}, {_session!.Usage}, {_saga.Usage}, {_saga.Usage}.{_idMember}, {_cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Deletes the completed saga document by <c>_id</c> on the Wolverine-managed session. The
/// <c>DetermineDeleteFrame</c> factory hands this frame the <c>sagaId</c> variable directly, so
/// the filter keys off it (not the saga document). Unguarded by version — see
/// <see cref="MongoSagaOperations.DeleteSagaAsync{TSaga,TId}"/>.
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
