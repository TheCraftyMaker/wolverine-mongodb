using MongoDB.Driver;

namespace Wolverine.MongoDB;

/// <summary>
/// Session-bound access to the application's MongoDB database inside a
/// Wolverine-managed outbox transaction. Every write performed through
/// <see cref="Collection{T}"/> automatically participates in the handler's
/// transaction — it is impossible to forget the session.
/// Handlers receive this as a parameter (the Wolverine.MongoDB code generation
/// constructs it from the open <see cref="IClientSessionHandle"/>).
/// </summary>
public class MongoDbUnitOfWork
{
    public MongoDbUnitOfWork(IClientSessionHandle session, IMongoDatabase database)
    {
        Session = session;
        Database = database;
    }

    /// <summary>The open session/transaction for this handler invocation.</summary>
    public IClientSessionHandle Session { get; }

    /// <summary>The raw database. Writes performed directly on this handle do NOT enlist.</summary>
    public IMongoDatabase Database { get; }

    public SessionBoundCollection<T> Collection<T>(string name)
        => new(Database.GetCollection<T>(name), Session);
}

/// <summary>
/// A write surface over <see cref="IMongoCollection{T}"/> that passes the
/// transaction session to every operation. Intentionally NOT an
/// IMongoCollection&lt;T&gt; implementation: only session-safe operations are exposed.
/// </summary>
public class SessionBoundCollection<T>
{
    private readonly IMongoCollection<T> _collection;
    private readonly IClientSessionHandle _session;

    public SessionBoundCollection(IMongoCollection<T> collection, IClientSessionHandle session)
    {
        _collection = collection;
        _session = session;
    }

    public Task InsertOneAsync(T document, CancellationToken ct = default)
        => _collection.InsertOneAsync(_session, document, cancellationToken: ct);

    public Task InsertManyAsync(IEnumerable<T> documents, CancellationToken ct = default)
        => _collection.InsertManyAsync(_session, documents, cancellationToken: ct);

    public Task<ReplaceOneResult> ReplaceOneAsync(FilterDefinition<T> filter, T replacement,
        ReplaceOptions? options = null, CancellationToken ct = default)
        => _collection.ReplaceOneAsync(_session, filter, replacement, options, ct);

    public Task<UpdateResult> UpdateOneAsync(FilterDefinition<T> filter, UpdateDefinition<T> update,
        UpdateOptions? options = null, CancellationToken ct = default)
        => _collection.UpdateOneAsync(_session, filter, update, options, ct);

    public Task<UpdateResult> UpdateManyAsync(FilterDefinition<T> filter, UpdateDefinition<T> update,
        UpdateOptions? options = null, CancellationToken ct = default)
        => _collection.UpdateManyAsync(_session, filter, update, options, ct);

    public Task<DeleteResult> DeleteOneAsync(FilterDefinition<T> filter, CancellationToken ct = default)
        => _collection.DeleteOneAsync(_session, filter, cancellationToken: ct);

    public Task<DeleteResult> DeleteManyAsync(FilterDefinition<T> filter, CancellationToken ct = default)
        => _collection.DeleteManyAsync(_session, filter, cancellationToken: ct);

    public Task<T> FindOneAndUpdateAsync(FilterDefinition<T> filter, UpdateDefinition<T> update,
        FindOneAndUpdateOptions<T>? options = null, CancellationToken ct = default)
        => _collection.FindOneAndUpdateAsync(_session, filter, update, options, ct);

    /// <summary>Transaction-consistent reads (sees this transaction's own writes).</summary>
    public IFindFluent<T, T> Find(FilterDefinition<T> filter)
        => _collection.Find(_session, filter);

    public IFindFluent<T, T> Find(System.Linq.Expressions.Expression<Func<T, bool>> filter)
        => _collection.Find(_session, filter);
}
