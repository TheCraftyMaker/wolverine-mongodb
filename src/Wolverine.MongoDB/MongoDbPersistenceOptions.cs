using JasperFx.Core.Reflection;
using Wolverine.MongoDB.Internals;

namespace Wolverine.MongoDB;

/// <summary>
/// MongoDB-specific persistence tuning for Wolverine.MongoDB.
/// </summary>
public class MongoDbPersistenceOptions
{
    private readonly Dictionary<Type, MongoCollectionMapping> _collectionMappings = new();

    /// <summary>
    /// How long the leader / scheduled-job lock lease is held before it can be
    /// taken over by another node. Wolverine's leadership health checks renew the
    /// lease well within this window. Lower values speed up leader failover at the
    /// cost of more lock churn; clocks across nodes must be synchronized to well
    /// within this duration. Default: 1 minute.
    /// </summary>
    public TimeSpan LockLeaseDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The explicit per-type collection mappings configured on this options instance, read back by
    /// <c>UseMongoDbPersistence</c>.
    /// </summary>
    internal IReadOnlyDictionary<Type, MongoCollectionMapping> CollectionMappings => _collectionMappings;

    /// <summary>
    /// Store <typeparamref name="TSaga"/> in an explicitly named MongoDB collection instead of the
    /// default <c>wolverine_saga_&lt;lowercased-type-name&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The default name is derived from the <i>simple</i> type name, so two saga types with the same
    /// name in different namespaces — or differing only in case, or two closed constructions of one
    /// open generic — resolve to the same collection. Wolverine.MongoDB refuses to start such a host;
    /// this is the escape hatch. The name must keep the <c>wolverine_saga_</c> prefix, because
    /// <c>IMessageStoreAdmin.ClearAllAsync</c>/<c>RebuildAsync</c> sweep saga collections by that prefix.
    /// Mapping a type does not move documents that were already written elsewhere.
    /// </remarks>
    /// <typeparam name="TSaga">The saga type to map.</typeparam>
    /// <param name="collectionName">The literal collection name, including the <c>wolverine_saga_</c> prefix.</param>
    /// <returns>This options instance, for chaining.</returns>
    /// <exception cref="ArgumentException">The name is empty, illegal for MongoDB, or outside the saga prefix.</exception>
    public MongoDbPersistenceOptions MapSagaCollection<TSaga>(string collectionName) where TSaga : Saga
        => MapSagaCollection(typeof(TSaga), collectionName);

    /// <summary>
    /// Store <paramref name="sagaType"/> in an explicitly named MongoDB collection instead of the
    /// default <c>wolverine_saga_&lt;lowercased-type-name&gt;</c>. The non-generic overload exists
    /// because the types most likely to need a mapping are closed generics and nested types.
    /// </summary>
    /// <param name="sagaType">The saga type to map. Must derive from <see cref="Saga"/>.</param>
    /// <param name="collectionName">The literal collection name, including the <c>wolverine_saga_</c> prefix.</param>
    /// <returns>This options instance, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="sagaType"/> is not a saga, or the name is empty, illegal for MongoDB, or outside
    /// the saga prefix.
    /// </exception>
    public MongoDbPersistenceOptions MapSagaCollection(Type sagaType, string collectionName)
    {
        ArgumentNullException.ThrowIfNull(sagaType);

        if (!sagaType.CanBeCastTo<Saga>())
        {
            throw new ArgumentException(
                $"{sagaType.FullNameInCode()} is not a Wolverine saga. Use MapEntityCollection for plain " +
                "document types.",
                nameof(sagaType));
        }

        return map(sagaType, collectionName, isSaga: true);
    }

    /// <summary>
    /// Store <typeparamref name="TEntity"/> in an explicitly named MongoDB collection instead of the
    /// default <c>&lt;lowercased-type-name&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The default name is derived from the <i>simple</i> type name, so two entity types with the same
    /// name in different namespaces — or differing only in case, or two closed constructions of one
    /// open generic — resolve to the same collection. Wolverine.MongoDB refuses to start such a host;
    /// this is the escape hatch. It is also the remedy when the application's own repositories already
    /// own the default name, which no startup check can see. Entity names may not sit inside the
    /// <c>wolverine_saga_</c> prefix (an administrative rebuild would drop them) or be one of Wolverine's
    /// system collections. Mapping a type does not move documents that were already written elsewhere.
    /// </remarks>
    /// <typeparam name="TEntity">The entity type to map.</typeparam>
    /// <param name="collectionName">The literal collection name.</param>
    /// <returns>This options instance, for chaining.</returns>
    /// <exception cref="ArgumentException">The name is empty, illegal for MongoDB, or reserved.</exception>
    public MongoDbPersistenceOptions MapEntityCollection<TEntity>(string collectionName) where TEntity : class
        => MapEntityCollection(typeof(TEntity), collectionName);

    /// <summary>
    /// Store <paramref name="entityType"/> in an explicitly named MongoDB collection instead of the
    /// default <c>&lt;lowercased-type-name&gt;</c>. The non-generic overload exists because the types
    /// most likely to need a mapping are closed generics and nested types.
    /// </summary>
    /// <param name="entityType">The entity type to map. Must not be a <see cref="Saga"/>.</param>
    /// <param name="collectionName">The literal collection name.</param>
    /// <returns>This options instance, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="entityType"/> is a saga, or the name is empty, illegal for MongoDB, or reserved.
    /// </exception>
    public MongoDbPersistenceOptions MapEntityCollection(Type entityType, string collectionName)
    {
        ArgumentNullException.ThrowIfNull(entityType);

        if (entityType.CanBeCastTo<Saga>())
        {
            throw new ArgumentException(
                $"{entityType.FullNameInCode()} is a Wolverine saga. Use MapSagaCollection so the name stays " +
                "inside the 'wolverine_saga_' prefix that administrative rebuilds sweep.",
                nameof(entityType));
        }

        return map(entityType, collectionName, isSaga: false);
    }

    private MongoDbPersistenceOptions map(Type type, string collectionName, bool isSaga)
    {
        MongoCollectionNaming.ValidateMappedName(type, collectionName, isSaga);

        if (_collectionMappings.TryGetValue(type, out var existing))
        {
            if (string.Equals(existing.CollectionName, collectionName, StringComparison.Ordinal))
            {
                return this;
            }

            throw new ArgumentException(
                $"{type.FullNameInCode()} is already mapped to MongoDB collection '{existing.CollectionName}' " +
                $"and cannot also be mapped to '{collectionName}'.",
                nameof(collectionName));
        }

        foreach (var pair in _collectionMappings)
        {
            if (string.Equals(pair.Value.CollectionName, collectionName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"MongoDB collection '{collectionName}' is already mapped to " +
                    $"{pair.Key.FullNameInCode()} and cannot also hold {type.FullNameInCode()} — an explicit " +
                    "mapping must not recreate the collision it exists to resolve.",
                    nameof(collectionName));
            }
        }

        _collectionMappings[type] = new MongoCollectionMapping(collectionName, isSaga);
        return this;
    }
}
