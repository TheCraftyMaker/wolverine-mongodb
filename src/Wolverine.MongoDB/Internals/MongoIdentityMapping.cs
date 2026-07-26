using System.Collections.Concurrent;
using System.Reflection;
using JasperFx.Core.Reflection;
using MongoDB.Bson.Serialization;
using Wolverine.Persistence.Sagas;

namespace Wolverine.MongoDB.Internals;

/// <summary>
/// Bridges Wolverine's identity-member convention (<c>SagaChain.DetermineSagaIdMember</c>:
/// <c>[SagaIdentity]</c> → <c>{TypeName}Id</c> → <c>{Name-minus-Saga}Id</c> → <c>SagaId</c> →
/// <c>Id</c>) and the MongoDB driver's independent one (<c>NamedIdMemberConvention</c>: only
/// <c>Id</c>/<c>id</c>/<c>_id</c>, plus <c>[BsonId]</c>) so that the frames' <c>Eq("_id", …)</c> read
/// filters and the written documents agree for every legal Wolverine identity convention.
///
/// <para>Without this bridge, a saga or entity keyed on e.g. <c>ShipmentId</c> auto-maps to
/// <b>no</b> id member at all, so the server assigns an unrelated <c>ObjectId</c> as <c>_id</c>: the
/// document can never be loaded back and every "start" accumulates another orphan.</para>
///
/// <para><b>Minimal mutation.</b> The BSON registry is left completely untouched whenever the
/// driver's own conventions already resolve the same member — every <c>Id</c>-keyed type <i>whether
/// that member is declared on the type or inherited</i> (i.e. every consumer that worked before this
/// fix, byte-identical), and every <c>[BsonId]</c>-annotated type at any level. Agreement is decided
/// by <see cref="driverIdMember"/>'s base-chain walk, <b>not</b> by a single unfrozen
/// <c>AutoMap()</c> of the document type: <c>AutoMap</c> maps only a class's own declared members, so
/// that reports nothing for an identity member declared on a base class — which is the shape of every
/// upstream saga compliance type. A class map is registered <i>only</i> for types that are otherwise
/// broken, and an already-registered map is never mutated or replaced (the driver forbids both) —
/// only asserted against. This is deliberately not the process-global serializer/convention
/// registration this library forswears: no serializer, no convention, no convention pack, and no
/// behavior change for any type Wolverine does not persist.</para>
///
/// <para><b>Two shapes it refuses instead of aligning</b>, both caught at codegen: an identity member
/// declared on a <b>base</b> type (the driver will not let a subclass's map claim it), and a
/// different, base-declared member already occupying <c>_id</c> (mapping ours would register cleanly
/// and then fail on the type's first write). Each throws with the verified remedies named.</para>
///
/// <para><b>Element naming.</b> Do not call <c>SetElementName("_id")</c>. <c>MapIdMember</c> leaves the
/// member map's element name as the member's own name; the driver rewrites it to <c>_id</c> during
/// <c>Freeze()</c>, and every class map is frozen before first use. That normalization is what lets
/// the raw <c>Eq("_id", …)</c> filters in <see cref="MongoSagaOperations"/> /
/// <see cref="MongoEntityOperations"/> stay exactly as they are.</para>
/// </summary>
internal static class MongoIdentityMapping
{
    private static readonly ConcurrentDictionary<Type, bool> _aligned = new();
    private static readonly object _gate = new();

    /// <summary>
    /// Wolverine's resolved identity member for <paramref name="documentType"/>. The single
    /// resolution point for sagas and entities alike; throws rather than guessing "Id".
    /// </summary>
    internal static MemberInfo ResolveIdMember(Type documentType)
        => SagaChain.DetermineSagaIdMember(documentType, documentType)
           ?? throw new ArgumentException(
               $"Unable to determine the identity member for {documentType.FullNameInCode()}",
               nameof(documentType));

    /// <summary>
    /// Resolve-then-ensure. Used where the caller has no <see cref="MemberInfo"/> in hand.
    /// </summary>
    internal static void EnsureIdMember(Type documentType)
    {
        if (_aligned.ContainsKey(documentType)) return;      // skip the reflection on the hot path
        EnsureIdMember(documentType, ResolveIdMember(documentType));
    }

    /// <summary>
    /// Ensures the MongoDB driver serializes <paramref name="idMember"/> as the document <c>_id</c>.
    /// No-op when the driver's own conventions already agree (member named <c>Id</c>/<c>id</c>/<c>_id</c>,
    /// <c>[BsonId]</c>, or an app-registered map with the same id member) — in that case the BSON
    /// registry is left untouched. Registers one additive per-type <see cref="BsonClassMap"/> when the
    /// type has no map and the conventions disagree. Throws when an already-registered map names a
    /// different id member.
    /// </summary>
    internal static void EnsureIdMember(Type documentType, MemberInfo idMember)
    {
        if (_aligned.ContainsKey(documentType)) return;

        // Deliberately NOT ConcurrentDictionary.GetOrAdd: its value factory may run more than once
        // concurrently for the same key, and BsonClassMap.RegisterClassMap is not idempotent (a
        // second call for one type throws a bare ArgumentException). One process-wide gate serializes
        // the compound check-then-register the driver's own per-call locking does not cover; the
        // pre-lock ContainsKey keeps the steady-state path lock-free.
        lock (_gate)
        {
            if (_aligned.ContainsKey(documentType)) return;
            align(documentType, idMember);

            // Reached only on success — a conflicting configuration must throw on every call rather
            // than throw once and then silently pass.
            _aligned[documentType] = true;
        }
    }

    private static void align(Type documentType, MemberInfo idMember)
    {
        if (BsonClassMap.IsClassMapRegistered(documentType))
        {
            assertRegisteredMapAgrees(documentType, idMember);
            return;
        }

        // What WILL the driver serialize as _id? Not "what does an unfrozen AutoMap of this one class
        // say" — that reports null for every inherited id member, which is the shape of every saga
        // whose identity member lives on a base class.
        var driverIdMap = driverIdMember(documentType);
        if (driverIdMap?.MemberName == idMember.Name)
        {
            // The driver already agrees. Do NOT register: leave the registry untouched so this type's
            // documents stay byte-identical and the application keeps its own right to register a
            // class map for it later.
            return;
        }

        // The driver refuses to map a member it does not own, and we can only register a map for
        // documentType itself, so an inherited identity member is beyond this mechanism's reach.
        if (idMember.DeclaringType != documentType)
        {
            throw inheritedIdentityMember(documentType, idMember);
        }

        // A different, BASE-declared member already claims _id. Mapping ours would register fine and
        // then throw BsonSerializationException on this type's first write, so refuse now. (A
        // different member declared on documentType itself is fine: MapIdMember re-points within the
        // one map and the previous id member demotes to an ordinary field.)
        if (driverIdMap != null && driverIdMap.MemberInfo.DeclaringType != documentType)
        {
            throw conflictingInheritedId(documentType, idMember, driverIdMap);
        }

        // The probe is brand new and unfrozen, so this can never hit the driver's
        // "class map has been frozen and no further changes are allowed" error.
        var probe = new BsonClassMap(documentType);
        probe.AutoMap();
        probe.MapIdMember(idMember);
        try
        {
            BsonClassMap.RegisterClassMap(probe);
        }
        catch (ArgumentException)
        {
            // Lost a race with application code that registered a map for this type between the
            // IsClassMapRegistered check above and this call. The winner's map decides; we only
            // assert agreement.
            assertRegisteredMapAgrees(documentType, idMember);
        }
    }

    /// <summary>
    /// What the MongoDB driver will actually serialize as <c>_id</c> for <paramref name="documentType"/>,
    /// or <c>null</c> if nothing will. Walks the base chain most-derived-first because
    /// <c>AutoMap()</c> maps only the members a class itself declares, while the driver's frozen map
    /// inherits its base map's id member — so a single unfrozen probe of the document type reports
    /// <c>null</c> for every inherited id member. Registers nothing: already-registered levels are
    /// read as they are, unregistered levels are auto-mapped on a throwaway map. Never called for
    /// <paramref name="documentType"/> when a map is already registered for it — <c>align</c> has
    /// returned by then.
    /// </summary>
    private static BsonMemberMap? driverIdMember(Type documentType)
    {
        for (var type = documentType; type != null && type != typeof(object); type = type.BaseType)
        {
            if (BsonClassMap.IsClassMapRegistered(type))
            {
                // A registered map with no id member does not end the search: the driver keeps
                // inheriting upward, so we do too.
                var registered = BsonClassMap.LookupClassMap(type);
                if (registered.IdMemberMap != null) return registered.IdMemberMap;
                continue;
            }

            var probe = new BsonClassMap(type);
            probe.AutoMap();
            if (probe.IdMemberMap != null) return probe.IdMemberMap;
        }

        return null;
    }

    /// <summary>
    /// Wolverine's identity member is declared on a base type, and the driver does not already map it.
    /// The driver rejects <c>MapIdMember</c> for a member whose <c>DeclaringType</c> is not the class
    /// map's own class, and a class map can only be registered for one type — so this mechanism cannot
    /// align it. Both remedies named in the message are verified working.
    /// </summary>
    private static InvalidOperationException inheritedIdentityMember(Type documentType, MemberInfo idMember)
    {
        var declaring = idMember.DeclaringType!;
        return new InvalidOperationException(
            $"Wolverine resolved '{idMember.Name}' as the identity member for " +
            $"{documentType.FullNameInCode()}, but that member is declared on the base type " +
            $"{declaring.FullNameInCode()}, and the MongoDB driver only lets a class map " +
            $"declare an id member of its own. Either put [BsonId] on " +
            $"{declaring.FullNameInCode()}.{idMember.Name}, or register a class map for " +
            $"{declaring.FullNameInCode()} that maps '{idMember.Name}' as the id member " +
            $"(BsonClassMap.RegisterClassMap<{declaring.Name}>(cm => {{ cm.AutoMap(); " +
            $"cm.MapIdMember(x => x.{idMember.Name}); }})). Either way every saga or entity inheriting " +
            "that member is fixed at once.");
    }

    /// <summary>
    /// A different, base-declared member already occupies <c>_id</c>. Mapping ours would register
    /// cleanly and then throw <c>BsonSerializationException</c> on the type's <b>first write</b> — at
    /// runtime, inside the outbox transaction — so refuse at codegen instead. The message deliberately
    /// does not suggest <c>[BsonId]</c>: on this shape it produces the identical serialization failure.
    /// </summary>
    private static InvalidOperationException conflictingInheritedId(
        Type documentType, MemberInfo idMember, BsonMemberMap driverIdMap)
    {
        var conflicting = driverIdMap.MemberInfo.DeclaringType!;
        return new InvalidOperationException(
            $"Wolverine resolved '{idMember.Name}' as the identity member for " +
            $"{documentType.FullNameInCode()}, but the MongoDB driver already maps " +
            $"'{driverIdMap.MemberName}' — inherited from " +
            $"{conflicting.FullNameInCode()} — as the document _id, and a " +
            "document cannot have two. Because the conflicting member belongs to a base type, this " +
            $"cannot be resolved by mapping '{idMember.Name}': the driver would accept the mapping and " +
            "then fail on the first write. Either stop the inherited member from being an id member " +
            $"([BsonIgnore] or [BsonElement(\"...\")] on " +
            $"{conflicting.FullNameInCode()}.{driverIdMap.MemberName}), or " +
            $"rename '{idMember.Name}' so Wolverine resolves the inherited member instead.");
    }

    private static void assertRegisteredMapAgrees(Type documentType, MemberInfo idMember)
    {
        var map = BsonClassMap.LookupClassMap(documentType);
        if (map.IdMemberMap?.MemberName == idMember.Name) return;

        throw new InvalidOperationException(
            $"The registered BsonClassMap for {documentType.FullNameInCode()} maps " +
            $"'{map.IdMemberMap?.MemberName ?? "(no id member)"}' as the document _id, but Wolverine " +
            $"resolved '{idMember.Name}' as the identity member for MongoDB persistence. Align them " +
            $"by putting [BsonId] on '{idMember.Name}', or by registering a class map that maps " +
            $"'{idMember.Name}' as the id member.");
    }
}
