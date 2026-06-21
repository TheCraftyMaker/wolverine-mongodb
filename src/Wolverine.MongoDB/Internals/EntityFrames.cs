using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Wolverine.Persistence;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// Generic (non-saga) entity read/write operations executed against the active Wolverine-managed
/// MongoDB session — the <c>[Entity]</c> parameter-load and <c>Insert&lt;T&gt;</c>/<c>Update&lt;T&gt;</c>/
/// <c>Store&lt;T&gt;</c>/<c>Delete&lt;T&gt;</c>/<c>IStorageAction&lt;T&gt;</c> return-value surface. The codegen
/// frames below emit calls into these helpers rather than inlining the driver calls directly:
/// <c>IMongoCollection&lt;T&gt;.Find(...)</c>, <c>FirstOrDefaultAsync(...)</c>, <c>ReplaceOneAsync(...)</c>
/// and <c>DeleteOneAsync(...)</c> are MongoDB.Driver <i>extension</i> methods, and the generated handler
/// does not carry a <c>using MongoDB.Driver;</c>. Centralizing the calls here keeps the generated code
/// free of extension-method/using concerns while guaranteeing every operation runs on the supplied
/// <see cref="IClientSessionHandle"/> — i.e. inside the same transaction as the outbox commit. Mirrors
/// the <c>MongoSagaOperations</c> / <c>CosmosDbStorageActionApplier</c> pattern.
///
/// <para>Unlike the saga helpers, plain entities carry no <c>Saga.Version</c>,
/// so writes are last-write-wins <b>upserts</b> with <b>no optimistic concurrency</b> (Cosmos/RavenDb
/// parity; D6 LD2). Apps that need document concurrency control use the repository +
/// <see cref="IClientSessionHandle"/> pattern, not this surface.</para>
/// </summary>
public static class MongoEntityOperations
{
    /// <summary>
    /// Load an entity document by its <c>_id</c> within the session/transaction. Returns
    /// <c>null</c> when no document matches — exactly the contract Wolverine core's <c>[Entity]</c>
    /// not-found / <c>Required</c> short-circuit expects (it owns the null-guard, the provider does not).
    /// </summary>
    public static Task<T> LoadAsync<T, TId>(
        IMongoDatabase database, IClientSessionHandle session, TId id, CancellationToken cancellationToken)
        where T : class
    {
        var collection = database.GetCollection<T>(MongoConstants.EntityCollectionName(typeof(T)));
        return collection
            .Find(session, Builders<T>.Filter.Eq("_id", id))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Session-less load for a read-only <c>[Entity]</c> handler that has no outbox transaction — e.g.
    /// an <c>[Entity]</c> parameter on a <c>Before</c> method whose handler returns no
    /// <c>Insert</c>/<c>Update</c>/<c>Store</c>/<c>Delete</c>/<c>IStorageAction&lt;T&gt;</c>, so nothing
    /// triggers <c>ApplyTransactionSupport</c> and no <see cref="IClientSessionHandle"/> exists. Reads
    /// directly off the DI-registered <see cref="IMongoDatabase"/> — the Mongo equivalent of how
    /// RavenDb/Cosmos read off their DI-registered session/container. (Loads are reads; only writes
    /// need the transaction. When a transaction <i>is</i> present, the session overload above is used.)
    /// </summary>
    public static Task<T> LoadAsync<T, TId>(
        IMongoDatabase database, TId id, CancellationToken cancellationToken)
        where T : class
    {
        var collection = database.GetCollection<T>(MongoConstants.EntityCollectionName(typeof(T)));
        return collection
            .Find(Builders<T>.Filter.Eq("_id", id))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Upsert an entity document within the session/transaction — the single write path for the
    /// <c>Insert</c>/<c>Update</c>/<c>Store</c> storage actions (D6 LD2: upsert-for-all, no OCC).
    /// <c>Insert&lt;T&gt;</c> deliberately upserts rather than <c>InsertOneAsync</c> so a redelivered
    /// message re-upserts the same document instead of throwing a duplicate-key error — the friendlier
    /// behavior for the at-least-once inbox. (Sagas keep <c>InsertOneAsync</c>'s duplicate-key guard as
    /// a concurrent-double-start defense; that path stays in <see cref="MongoSagaOperations"/>.)
    /// </summary>
    public static Task UpsertAsync<T>(
        IMongoDatabase database, IClientSessionHandle session, T entity, CancellationToken cancellationToken)
        where T : class
    {
        var collection = database.GetCollection<T>(MongoConstants.EntityCollectionName(typeof(T)));
        return collection.ReplaceOneAsync(
            session,
            Builders<T>.Filter.Eq("_id", IdOf(entity)),
            entity,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    /// <summary>
    /// Delete an entity document by its <c>_id</c> within the session/transaction. The <c>_id</c> value
    /// is extracted from the entity via the BSON class map (<see cref="IdOf{T}"/>), not the saga path's
    /// separate id variable — the <c>Delete&lt;T&gt;</c> return value <i>is</i> the entity.
    /// </summary>
    public static Task DeleteAsync<T>(
        IMongoDatabase database, IClientSessionHandle session, T entity, CancellationToken cancellationToken)
        where T : class
    {
        var collection = database.GetCollection<T>(MongoConstants.EntityCollectionName(typeof(T)));
        return collection.DeleteOneAsync(
            session,
            Builders<T>.Filter.Eq("_id", IdOf(entity)),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Apply a single <see cref="IStorageAction{T}"/> within the session/transaction — the generic
    /// return-value path (<c>IStorageAction&lt;T&gt;</c> and each element of a <c>UnitOfWork&lt;T&gt;</c>).
    /// A null entity (e.g. a returned-null action) is a no-op, as is <see cref="StorageAction.Nothing"/>.
    /// </summary>
    public static Task ApplyStorageActionAsync<T>(
        IMongoDatabase database, IClientSessionHandle session, IStorageAction<T> action, CancellationToken cancellationToken)
        where T : class
    {
        if (action.Entity is null)
        {
            return Task.CompletedTask;
        }

        return action.Action switch
        {
            StorageAction.Delete => DeleteAsync(database, session, action.Entity, cancellationToken),
            StorageAction.Insert or StorageAction.Update or StorageAction.Store
                => UpsertAsync(database, session, action.Entity, cancellationToken),
            _ => Task.CompletedTask, // Nothing
        };
    }

    /// <summary>
    /// Resolve an entity's <c>_id</c> value generically via the MongoDB driver class map — the
    /// idiomatic-Mongo equivalent of asking the driver "what does this type map to <c>_id</c>?".
    /// This honors the driver's default Id-member convention (e.g. <c>Todo.Id</c>) and works for every
    /// native id type (<c>string</c>/<c>Guid</c>/<c>int</c>/<c>long</c>) with no per-type code. It is
    /// explicitly <b>not</b> Cosmos's <c>entity.ToString()</c> hack, which would be wrong for a POCO
    /// whose <c>ToString()</c> is its type name. The throw surfaces a clear, early error if generic
    /// persistence is registered for a type with no mapped id member.
    /// </summary>
    private static object IdOf<T>(T entity)
        => BsonClassMap.LookupClassMap(typeof(T)).IdMemberMap?.Getter(entity)
           ?? throw new InvalidOperationException(
               $"{typeof(T).FullNameInCode()} has no mapped _id member; generic MongoDB persistence requires an Id member.");
}

/// <summary>
/// Loads an entity document from MongoDB by <c>_id</c> on the Wolverine-managed session for the
/// <c>[Entity]</c> parameter-load path. Exposes the loaded entity as its single created
/// <see cref="Variable"/>, which Wolverine core consumes via
/// <c>frame.Creates.First(x =&gt; x.VariableType == parameterType)</c> (EntityAttribute.cs:167) — the
/// same contract the saga <c>LoadSagaFrame</c> and RavenDb's <c>LoadDocumentFrame</c> satisfy. Returns
/// <c>null</c> when the document is absent; core owns the not-found / <c>Required</c> short-circuit.
/// </summary>
internal class LoadEntityFrame : AsyncFrame
{
    private readonly Variable _id;
    private Variable? _cancellation;
    private Variable? _database;
    private Variable? _session;

    public LoadEntityFrame(Type entityType, Variable id)
    {
        _id = id;
        uses.Add(id);
        Entity = new Variable(entityType, this);
    }

    public Variable Entity { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _database = chain.FindVariable(typeof(IMongoDatabase));
        yield return _database;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        // The session is only present when an outbox transaction was applied — a write-back storage
        // action (Insert/Update/Store/Delete/IStorageAction<T>) triggers ApplyTransactionSupport. A
        // read-only [Entity] handler (e.g. an [Entity] in a Before method with no write-back) has
        // none, so resolve it non-forcingly: when present, load on the session (consistent with the
        // write inside the same transaction); when absent, the session-less LoadAsync overload reads
        // off IMongoDatabase directly. NotServices keeps this non-forcing — it never injects a session.
        _session = chain.TryFindVariable(typeof(IClientSessionHandle), VariableSource.NotServices);
        if (_session is not null)
        {
            yield return _session;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.BlankLine();
        writer.WriteComment("Load the entity document by _id (on the MongoDB session when inside a transaction; null if it does not exist)");
        var sessionArg = _session is null ? "" : $"{_session.Usage}, ";
        writer.Write(
            $"{Entity.VariableType.FullNameInCode()} {Entity.Usage} = await {typeof(MongoEntityOperations).FullNameInCode()}.{nameof(MongoEntityOperations.LoadAsync)}<{Entity.VariableType.FullNameInCode()}, {_id.VariableType.FullNameInCode()}>({_database!.Usage}, {sessionArg}{_id.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Upserts an entity document on the Wolverine-managed session (inside the outbox transaction) — the
/// write frame for the <c>Insert&lt;T&gt;</c>/<c>Update&lt;T&gt;</c>/<c>Store&lt;T&gt;</c> entity branch.
/// Last-write-wins; no version guard (entities carry no <c>Saga.Version</c>).
/// </summary>
internal class MongoUpsertEntityFrame : AsyncFrame
{
    private readonly Variable _entity;
    private Variable? _cancellation;
    private Variable? _database;
    private Variable? _session;

    public MongoUpsertEntityFrame(Variable entity)
    {
        _entity = entity;
        uses.Add(entity);
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
        writer.WriteComment("Upsert the entity document on the MongoDB session (inside the outbox transaction)");
        writer.Write(
            $"await {typeof(MongoEntityOperations).FullNameInCode()}.{nameof(MongoEntityOperations.UpsertAsync)}<{_entity.VariableType.FullNameInCode()}>({_database!.Usage}, {_session!.Usage}, {_entity.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// Deletes an entity document on the Wolverine-managed session (inside the outbox transaction) — the
/// frame for the generic single-variable <c>Delete&lt;T&gt;</c> return value. The handed variable
/// <i>is</i> the entity (core's <c>EntityVariable</c>), so the <c>_id</c> filter value is extracted from
/// it via the BSON class map inside <see cref="MongoEntityOperations.DeleteAsync{T}"/> — not the saga
/// path's separate id variable, and not Cosmos's <c>ToString()</c>.
/// </summary>
internal class MongoDeleteEntityByVariableFrame : AsyncFrame
{
    private readonly Variable _entity;
    private Variable? _cancellation;
    private Variable? _database;
    private Variable? _session;

    public MongoDeleteEntityByVariableFrame(Variable entity)
    {
        _entity = entity;
        uses.Add(entity);
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
        writer.WriteComment("Delete the entity document by _id on the MongoDB session (inside the outbox transaction)");
        writer.Write(
            $"await {typeof(MongoEntityOperations).FullNameInCode()}.{nameof(MongoEntityOperations.DeleteAsync)}<{_entity.VariableType.FullNameInCode()}>({_database!.Usage}, {_session!.Usage}, {_entity.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }
}
