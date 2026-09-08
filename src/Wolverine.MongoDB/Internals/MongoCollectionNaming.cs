using System.Collections.Concurrent;
using System.Text;
using JasperFx.Core.Reflection;
using MongoDB.Driver;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// A saga or entity type's explicitly mapped collection name.
/// </summary>
/// <param name="CollectionName">The literal MongoDB collection name to use.</param>
/// <param name="IsSaga">
/// <c>true</c> when the mapped type is a <see cref="Saga"/>. Saga and entity names obey different
/// legality rules (see <see cref="MongoCollectionNaming.ValidateMappedName"/>), so the kind travels
/// with the mapping.
/// </param>
internal readonly record struct MongoCollectionMapping(string CollectionName, bool IsSaga);

/// <summary>
/// The single resolution point for "which MongoDB collection does this saga or entity type live in".
/// Layers two things over <see cref="MongoConstants"/>' default naming functions, which stay exactly
/// as they were:
/// <list type="number">
/// <item><description>
/// <b>Explicit per-type mappings</b>, registered from
/// <c>MongoDbPersistenceOptions.MapSagaCollection</c>/<c>MapEntityCollection</c> through
/// <see cref="ApplyMappings"/>.
/// </description></item>
/// <item><description>
/// <b>Collision detection.</b> <see cref="MongoConstants.SagaCollectionName"/> and
/// <see cref="MongoConstants.EntityCollectionName"/> derive the name from <c>Type.Name</c> lowercased,
/// which carries no namespace, no generic arguments and no case. Unrelated types with the same simple
/// name (<c>Ordering.Note</c> / <c>Billing.Note</c>), types differing only by case, and different
/// closed constructions of one open generic (<c>Box&lt;int&gt;</c> and <c>Box&lt;string&gt;</c> are
/// both <c>box`1</c>) therefore resolve to one collection. Nothing configures a <c>_t</c>
/// discriminator and entity writes upsert with no version guard, so such types silently mix or clobber
/// each other's documents. <see cref="ClaimSaga(string,Type)"/>/<see cref="ClaimEntity(string,Type)"/>
/// record the first type to take a name and throw when a second, different type wants it.
/// </description></item>
/// </list>
///
/// <para><b>Why the state is static.</b> The runtime collection accessors
/// (<c>MongoSagaOperations</c>/<c>MongoEntityOperations</c>) are <c>static</c> and receive only an
/// <see cref="IMongoDatabase"/> — every generated call site passes <c>(database, session, …)</c> and
/// nothing else. Threading an options object through them would change the signatures of two
/// <c>public static</c> helper classes that pre-generated (<c>TypeLoadMode.Static</c>) deployments
/// were compiled against. This mirrors <see cref="MongoIdentityMapping"/>, which solved the same
/// codegen-time-plus-runtime problem the same way.</para>
///
/// <para><b>Scoping and idempotency.</b> Both registries are keyed by <b>database name</b>, so two
/// hosts in one process pointed at different databases never interact — the invariant is only "two
/// hosts writing to the same database in the same process must agree on naming", which they must
/// anyway because they share the physical collections. Re-registering the same mapping is a silent
/// no-op and a type re-claiming a collection it already owns is a silent no-op, so repeated and
/// concurrent host construction (the compliance suites, the two-in-proc-host multinode fixtures)
/// costs nothing but a dictionary probe.</para>
///
/// <para><b>What "disagreement" does and does not catch.</b> Two hosts that each configure a mapping
/// for one type in one database and disagree throw, rather than letting whichever started first win.
/// A host that configures <i>no</i> mapping is the weaker case: nothing here distinguishes "left at
/// its default" from "never mentioned", so an unconfigured host silently inherits whichever mapping
/// another host in the process published for the type. The asymmetry is one-way — a host that
/// actually <i>resolved</i> the type to its default name first makes the later mapping throw
/// (<see cref="ApplyMappings"/>) — and the inheriting direction is the benign one, since both hosts
/// share the database and must therefore share the name. It is still inheritance, not agreement.</para>
///
/// <para><b>Reserved names.</b> Saga mappings must keep the <see cref="MongoConstants.SagaCollectionPrefix"/>
/// prefix, because <c>IMessageStoreAdmin.ClearAllAsync</c>/<c>RebuildAsync</c> sweep saga collections by
/// that prefix (<c>MongoDbMessageStore.Admin.cs</c>) — a saga moved outside it would silently stop being
/// cleared. Entity names must stay <i>outside</i> that prefix and off the nine Wolverine system
/// collections, for the mirror-image reason: an entity inside the prefix would have its application data
/// dropped by every <c>RebuildAsync</c>, and an entity on a system name would interleave documents with
/// Wolverine's own state.</para>
/// </summary>
internal static class MongoCollectionNaming
{
    /// <summary>
    /// The explicit per-type mappings, held as an immutable snapshot and replaced wholesale. Copy-on-write
    /// rather than a <see cref="ConcurrentDictionary{TKey,TValue}"/> so that <see cref="ApplyMappings"/> can
    /// validate a whole set and then make it visible in a single reference assignment: a rejected set is
    /// never published, not even in part, and no reader can observe half of an accepted one. Readers
    /// (<see cref="resolve"/>, which the runtime collection accessors reach on every load/upsert/delete)
    /// take no lock — they read the field once and look up a dictionary that is never mutated again.
    /// </summary>
    private static volatile Dictionary<(string Database, Type Type), string> _mappings = new();

    private static readonly ConcurrentDictionary<(string Database, string Collection), Type> _claims = new();

    /// <summary>
    /// Serializes <see cref="ApplyMappings"/> against itself, so validating a set and publishing it is one
    /// atomic step. Taken only on the cold configuration path — one call per host build — and never by
    /// <see cref="resolve"/> or <see cref="claim"/>.
    /// </summary>
    private static readonly Lock _mappingLock = new();

    private static readonly HashSet<string> _reservedNames = new(StringComparer.Ordinal)
    {
        MongoConstants.IncomingCollection,
        MongoConstants.OutgoingCollection,
        MongoConstants.DeadLetterCollection,
        MongoConstants.NodeCollection,
        MongoConstants.NodeAssignmentCollection,
        MongoConstants.NodeRecordCollection,
        MongoConstants.AgentRestrictionCollection,
        MongoConstants.CounterCollection,
        MongoConstants.LockCollection
    };

    /// <summary>
    /// Whether <paramref name="collectionName"/> is inside the per-saga-type collection prefix. The
    /// single predicate shared by the mapping validator and <c>MongoDbMessageStore.Admin</c>'s sweep,
    /// so the sweep and the names the resolver hands out cannot drift apart.
    /// </summary>
    internal static bool IsSagaCollectionName(string collectionName)
        => collectionName.StartsWith(MongoConstants.SagaCollectionPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Validates an explicitly mapped collection name. Called from the
    /// <c>MongoDbPersistenceOptions.Map*Collection</c> setters so a bad name fails at configuration
    /// time, next to the mistake, rather than at host start.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty, illegal for MongoDB, or reserved.</exception>
    internal static void ValidateMappedName(Type type, string collectionName, bool isSaga)
    {
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            throw new ArgumentException(
                $"The MongoDB collection name mapped for {type.FullNameInCode()} cannot be empty or whitespace.",
                nameof(collectionName));
        }

        if (collectionName.Contains('$') || collectionName.Contains('\0'))
        {
            throw new ArgumentException(
                $"'{collectionName}' is not a legal MongoDB collection name (mapped for " +
                $"{type.FullNameInCode()}): collection names may not contain '$' or a null character.",
                nameof(collectionName));
        }

        if (collectionName.StartsWith("system.", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"'{collectionName}' is not a legal MongoDB collection name (mapped for " +
                $"{type.FullNameInCode()}): the 'system.' prefix is reserved by MongoDB.",
                nameof(collectionName));
        }

        if (isSaga && !IsSagaCollectionName(collectionName))
        {
            throw new ArgumentException(
                $"The saga collection mapped for {type.FullNameInCode()} must start with " +
                $"'{MongoConstants.SagaCollectionPrefix}' (got '{collectionName}'). " +
                "IMessageStoreAdmin.ClearAllAsync/RebuildAsync drop saga collections by that prefix, so a saga " +
                $"stored outside it would silently stop being cleared. Try " +
                $"'{MongoConstants.SagaCollectionPrefix}{collectionName}'.",
                nameof(collectionName));
        }

        if (!isSaga && IsSagaCollectionName(collectionName))
        {
            throw new ArgumentException(
                $"The entity collection mapped for {type.FullNameInCode()} must not start with " +
                $"'{MongoConstants.SagaCollectionPrefix}' (got '{collectionName}'). That prefix is swept by " +
                "IMessageStoreAdmin.ClearAllAsync/RebuildAsync, which would drop this application data.",
                nameof(collectionName));
        }

        if (!isSaga && _reservedNames.Contains(collectionName))
        {
            throw new ArgumentException(
                $"'{collectionName}' is a Wolverine system collection and cannot be mapped for " +
                $"{type.FullNameInCode()}. Application documents written there would interleave with " +
                "Wolverine's own inbox/outbox/node state.",
                nameof(collectionName));
        }
    }

    /// <summary>
    /// Registers the explicit mappings configured for one database. Called synchronously from
    /// <c>UseMongoDbPersistence</c>, before the handler graph is compiled and before any collection
    /// accessor can run for that host. Re-applying an identical mapping set is a lock-free no-op.
    /// </summary>
    /// <remarks>
    /// <b>All-or-nothing.</b> The entire set is validated before any of it is published, and publication is
    /// a single reference assignment, so a throw leaves the registry exactly as it was. That matters because
    /// <see cref="_mappings"/> is process-global and is read on the runtime hot path: a mapping that was
    /// published and only <i>then</i> rejected would redirect an already-running host — one that built
    /// successfully because it configured no mapping, and has been reading and writing the default
    /// collection ever since — to a different, empty collection, orphaning its documents with no exception
    /// raised anywhere on that host.
    /// </remarks>
    /// <param name="databaseName">The database these mappings apply to.</param>
    /// <param name="mappings">The mappings configured on one <c>MongoDbPersistenceOptions</c>.</param>
    /// <exception cref="ArgumentException">
    /// One of the names is empty, illegal for MongoDB, or reserved.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Two types in the set want the same collection, or another host in this process already mapped one of
    /// the types in the same database to a different collection, or already resolved it to its default name.
    /// </exception>
    internal static void ApplyMappings(string databaseName, IReadOnlyDictionary<Type, MongoCollectionMapping> mappings)
    {
        if (mappings.Count == 0 || alreadyPublished(databaseName, mappings)) return;

        lock (_mappingLock)
        {
            var published = _mappings;

            // Phase 1 — validate the whole set. Nothing in this loop writes to the registry, so a throw
            // from any iteration leaves it byte-identical to what it was.
            var namesInSet = new Dictionary<string, Type>(StringComparer.Ordinal);

            foreach (var pair in mappings)
            {
                var type = pair.Key;
                var mapping = pair.Value;

                ValidateMappedName(type, mapping.CollectionName, mapping.IsSaga);

                if (namesInSet.TryGetValue(mapping.CollectionName, out var sibling))
                {
                    throw new InvalidOperationException(
                        $"MongoDB collection '{mapping.CollectionName}' in database '{databaseName}' is mapped " +
                        $"for both {sibling.FullNameInCode()} and {type.FullNameInCode()} — an explicit mapping " +
                        "must not recreate the collision it exists to resolve.");
                }

                namesInSet[mapping.CollectionName] = type;

                if (published.TryGetValue((databaseName, type), out var existing)
                    && !string.Equals(existing, mapping.CollectionName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"{type.FullNameInCode()} is already mapped to MongoDB collection '{existing}' in database " +
                        $"'{databaseName}' by another Wolverine host in this process, and cannot also be mapped to " +
                        $"'{mapping.CollectionName}'. Collection naming is a property of the (process, database) pair: " +
                        "two hosts writing to the same database must agree on where a type is stored.");
                }

                var defaultName = DefaultName(type, mapping.IsSaga);
                if (!string.Equals(defaultName, mapping.CollectionName, StringComparison.Ordinal)
                    && _claims.TryGetValue((databaseName, defaultName), out var claimant)
                    && claimant == type)
                {
                    throw new InvalidOperationException(
                        $"{type.FullNameInCode()} has already been resolved to its default MongoDB collection " +
                        $"'{defaultName}' in database '{databaseName}' earlier in this process, so it cannot now be " +
                        $"mapped to '{mapping.CollectionName}'. Configure the mapping on every Wolverine host that " +
                        "persists this type into this database.");
                }
            }

            // Phase 2 — publish. Every entry is either absent or already equal to what is being written
            // (phase 1 proved it), and the lock keeps another ApplyMappings from interleaving, so this
            // cannot overwrite a mapping somebody else validated against.
            var updated = new Dictionary<(string Database, Type Type), string>(published);
            foreach (var pair in mappings)
            {
                updated[(databaseName, pair.Key)] = pair.Value.CollectionName;
            }

            _mappings = updated;
        }
    }

    /// <summary>
    /// Whether every mapping in the set is already published exactly as asked. The lock-free fast path that
    /// keeps repeated host construction cheap — the compliance suites build many hosts against one database
    /// and the multinode fixtures build two at once. Skipping validation here cannot skip a check that would
    /// have failed: a set only becomes visible once it has passed every check, validation is a pure function
    /// of the (type, name, kind) triple, and once a type is mapped nothing can go on to claim its default
    /// name.
    /// </summary>
    private static bool alreadyPublished(
        string databaseName, IReadOnlyDictionary<Type, MongoCollectionMapping> mappings)
    {
        var published = _mappings;

        foreach (var pair in mappings)
        {
            if (!published.TryGetValue((databaseName, pair.Key), out var current)
                || !string.Equals(current, pair.Value.CollectionName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The default, un-overridden collection name for a type.</summary>
    internal static string DefaultName(Type type, bool isSaga)
        => isSaga ? MongoConstants.SagaCollectionName(type) : MongoConstants.EntityCollectionName(type);

    /// <summary>
    /// The collection a saga type is stored in — mapping-aware, but <b>without</b> taking a collision
    /// claim. Used by read-only surfaces (<c>MongoDbSagaStoreDiagnostics</c>) that must honour a mapping
    /// but must never throw a configuration error at a diagnostics caller.
    /// </summary>
    internal static string ResolveSaga(string databaseName, Type sagaType)
        => resolve(databaseName, sagaType, isSaga: true);

    /// <summary>
    /// The collection an entity type is stored in — mapping-aware, without taking a collision claim.
    /// </summary>
    internal static string ResolveEntity(string databaseName, Type entityType)
        => resolve(databaseName, entityType, isSaga: false);

    /// <summary>Resolve a saga collection and claim it for <paramref name="sagaType"/>.</summary>
    internal static string ClaimSaga(string databaseName, Type sagaType)
        => claim(databaseName, sagaType, isSaga: true);

    /// <summary>Resolve an entity collection and claim it for <paramref name="entityType"/>.</summary>
    internal static string ClaimEntity(string databaseName, Type entityType)
        => claim(databaseName, entityType, isSaga: false);

    /// <summary>Resolve a saga collection and claim it, taking the database name off the handle.</summary>
    internal static string ClaimSaga(IMongoDatabase database, Type sagaType)
        => claim(database.DatabaseNamespace.DatabaseName, sagaType, isSaga: true);

    /// <summary>Resolve an entity collection and claim it, taking the database name off the handle.</summary>
    internal static string ClaimEntity(IMongoDatabase database, Type entityType)
        => claim(database.DatabaseNamespace.DatabaseName, entityType, isSaga: false);

    private static string resolve(string databaseName, Type type, bool isSaga)
    {
        if (_mappings.TryGetValue((databaseName, type), out var mapped))
        {
            return mapped;
        }

        var name = DefaultName(type, isSaga);
        assertDefaultIsUsable(type, name, isSaga);
        return name;
    }

    private static string claim(string databaseName, Type type, bool isSaga)
    {
        var name = resolve(databaseName, type, isSaga);

        // GetOrAdd is atomic, so concurrent host construction cannot see a torn state, and a type
        // re-claiming the collection it already owns is a lock-free no-op.
        var winner = _claims.GetOrAdd((databaseName, name), type);
        if (winner != type)
        {
            throw collision(databaseName, name, winner, type, isSaga);
        }

        return name;
    }

    /// <summary>
    /// A <i>default</i> name derived from the type's own simple name can still land somewhere
    /// destructive: an entity type named <c>Wolverine_saga_Foo</c> resolves into the saga prefix and is
    /// dropped by every <c>RebuildAsync</c>, and one named <c>Wolverine_nodes</c> interleaves its
    /// documents with the node registry. Both are absurd in practice and both are silently destructive
    /// today, so they are refused here rather than mapped.
    /// </summary>
    private static void assertDefaultIsUsable(Type type, string name, bool isSaga)
    {
        if (isSaga) return;

        if (IsSagaCollectionName(name))
        {
            throw new InvalidOperationException(
                $"{type.FullNameInCode()} would be stored in MongoDB collection '{name}', which is inside the " +
                $"'{MongoConstants.SagaCollectionPrefix}' prefix that IMessageStoreAdmin.ClearAllAsync/RebuildAsync " +
                "sweeps — this application data would be dropped by any administrative rebuild. Give the type an " +
                $"explicit collection: o.MapEntityCollection(typeof({type.FullNameInCode()}), \"{suggest(type)}\").");
        }

        if (_reservedNames.Contains(name))
        {
            throw new InvalidOperationException(
                $"{type.FullNameInCode()} would be stored in MongoDB collection '{name}', which is a Wolverine " +
                "system collection — its documents would interleave with Wolverine's own inbox/outbox/node state. " +
                $"Give the type an explicit collection: o.MapEntityCollection(typeof({type.FullNameInCode()}), " +
                $"\"{suggest(type)}\").");
        }
    }

    private static InvalidOperationException collision(
        string databaseName, string collectionName, Type existing, Type incoming, bool isSaga)
    {
        var mapCall = isSaga ? "MapSagaCollection" : "MapEntityCollection";
        var suggestion = isSaga ? MongoConstants.SagaCollectionPrefix + suggest(incoming) : suggest(incoming);

        return new InvalidOperationException(
            $"MongoDB collection '{collectionName}' in database '{databaseName}' would be shared by two different " +
            $"Wolverine-persisted types: {existing.FullNameInCode()} and {incoming.FullNameInCode()}. " +
            "Wolverine.MongoDB derives collection names from the simple type name (Type.Name, lowercased), which " +
            "carries no namespace, no generic arguments and no case, so these two types would silently mix " +
            "documents in one collection: nothing writes a type discriminator, and entity writes upsert with no " +
            "version guard, so one type's documents can replace the other's. Give one of them an explicit " +
            $"collection name, for example: opts.UseMongoDbPersistence(\"{databaseName}\", o => " +
            $"o.{mapCall}(typeof({incoming.FullNameInCode()}), \"{suggestion}\")); " +
            "Documents already written into the shared collection are not moved by that mapping.");
    }

    /// <summary>
    /// A namespace-qualified, MongoDB-legal suggestion for an explicit mapping, so the exception text is
    /// copy-pasteable. Purely cosmetic — nothing depends on the value.
    /// </summary>
    private static string suggest(Type type)
    {
        var source = type.FullNameInCode().ToLowerInvariant();
        var builder = new StringBuilder(source.Length);

        foreach (var c in source)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        var suggestion = builder.ToString().Trim('_');
        return suggestion.Length == 0 ? "collection_name" : suggestion;
    }
}
