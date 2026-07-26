using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Attributes;
using Shouldly;
using Wolverine.MongoDB.Internals;
using Xunit;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// F6 — unit coverage for <c>MongoIdentityMapping</c>'s decision branches, which the integration
/// facts in <c>saga_identity_conventions.cs</c> cannot reach: the two no-op paths (where the driver's
/// own conventions already agree and the BSON registry must be left <b>untouched</b>), the two
/// throwing paths, idempotency, and the concurrent-first-call path.
///
/// <para><b>Every fact owns its document type outright.</b> Class maps cannot be unregistered or
/// replaced, and the helper's alignment memo is process-global — a type touched by two facts would
/// make one of them observe state the other created. None of these types is referenced anywhere else
/// in the assembly.</para>
/// </summary>
public class identity_mapping_helper
{
    // ── row 6: the driver already agrees (Id-named member) → registry untouched ────────

    [Fact]
    public void plain_id_member_is_a_no_op_that_leaves_the_registry_untouched()
    {
        BsonClassMap.IsClassMapRegistered(typeof(HelperPlainIdDoc)).ShouldBeFalse();

        MongoIdentityMapping.EnsureIdMember(typeof(HelperPlainIdDoc));

        // The whole regression argument for existing consumers: an Id-keyed document type is never
        // registered by us, so its serialization — and the app's right to register its own map
        // later — are exactly as they were.
        BsonClassMap.IsClassMapRegistered(typeof(HelperPlainIdDoc)).ShouldBeFalse();
    }

    // ── row 7: [BsonId] on a non-Id-named member → also a no-op ───────────────────────

    [Fact]
    public void bson_id_attributed_member_is_a_no_op()
    {
        BsonClassMap.IsClassMapRegistered(typeof(HelperBsonIdDoc)).ShouldBeFalse();

        // Wolverine resolves HelperBsonIdDocId ({TypeName}Id) and the driver's AutoMap resolves the
        // same member from [BsonId] — they agree, so no registration is needed.
        MongoIdentityMapping.EnsureIdMember(typeof(HelperBsonIdDoc));

        BsonClassMap.IsClassMapRegistered(typeof(HelperBsonIdDoc)).ShouldBeFalse();
    }

    // ── row 8: a registered map naming a different id member → precise throw ──────────

    [Fact]
    public void conflicting_registered_class_map_throws_naming_both_members()
    {
        // The app got there first and mapped `Id` as the document id; Wolverine resolves
        // HelperConflictDocId ({TypeName}Id outranks Id). The driver forbids replacing or thawing a
        // registered map, so the only sound contract is "assert agreement or throw".
        var appMap = new BsonClassMap<HelperConflictDoc>();
        appMap.AutoMap();
        BsonClassMap.RegisterClassMap(appMap);

        var ex = Should.Throw<InvalidOperationException>(
            () => MongoIdentityMapping.EnsureIdMember(typeof(HelperConflictDoc)));

        ex.Message.ShouldContain(nameof(HelperConflictDoc));
        ex.Message.ShouldContain("'Id'");                                   // what the driver has
        ex.Message.ShouldContain($"'{nameof(HelperConflictDoc.HelperConflictDocId)}'"); // what Wolverine resolved
        ex.Message.ShouldContain("[BsonId]");                               // the remedy

        // Failures are deliberately not memoized — a misconfiguration must throw every time rather
        // than throw once and silently pass afterwards.
        Should.Throw<InvalidOperationException>(
            () => MongoIdentityMapping.EnsureIdMember(typeof(HelperConflictDoc)));
    }

    // ── row 9: unresolvable identity member → ArgumentException, one message ──────────

    [Fact]
    public void unresolvable_identity_member_throws_the_same_message_as_the_provider()
    {
        // Asserted on the prefix, never on ParamName or the full formatted string (design §5).
        Should.Throw<ArgumentException>(() => MongoIdentityMapping.ResolveIdMember(typeof(HelperNoIdDoc)))
            .Message.ShouldStartWith("Unable to determine the identity member for");

        Should.Throw<ArgumentException>(() => MongoIdentityMapping.EnsureIdMember(typeof(HelperNoIdDoc)))
            .Message.ShouldContain(nameof(HelperNoIdDoc));
    }

    // ── row 10: concurrent first calls → exactly one registration, nothing escapes ────

    [Fact]
    public void concurrent_first_calls_register_exactly_once()
    {
        const int threads = 8;
        var barrier = new Barrier(threads);
        var failures = new List<Exception>();

        Parallel.For(0, threads, _ =>
        {
            barrier.SignalAndWait();
            try
            {
                MongoIdentityMapping.EnsureIdMember(typeof(HelperConcurrentDoc));
            }
            catch (Exception e)
            {
                lock (failures) failures.Add(e);
            }
        });

        // RegisterClassMap is NOT idempotent — a second call for the same type throws a bare
        // ArgumentException. Nothing may escape to a caller.
        failures.ShouldBeEmpty();

        var map = BsonClassMap.LookupClassMap(typeof(HelperConcurrentDoc));
        map.IdMemberMap.ShouldNotBeNull();
        map.IdMemberMap!.MemberName.ShouldBe(nameof(HelperConcurrentDoc.HelperConcurrentDocId));
    }

    // ── row 19: an INHERITED Id is a no-op, registry untouched at every level ─────────

    /// <summary>
    /// The regression guard. <c>AutoMap()</c> maps only a class's <i>own</i> declared members, so a
    /// single unfrozen probe of the document type reports no id member for an identity member declared
    /// on a base class — the shape of every upstream compliance saga
    /// (<c>BasicWorkflow&lt;TStart,TCompleteThree,TId&gt;.Id</c>). Reading it that way made the helper
    /// conclude "the driver disagrees" and mutate the registry for types that already worked. The
    /// base-chain walk resolves the inherited member, so this is a pure no-op — and it must be one at
    /// <b>every</b> level, including the closed generic base and the root.
    /// </summary>
    [Fact]
    public void inherited_id_member_is_a_no_op_at_every_level_of_the_hierarchy()
    {
        MongoIdentityMapping.EnsureIdMember(typeof(Row19Derived));

        BsonClassMap.IsClassMapRegistered(typeof(Row19Derived)).ShouldBeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(Row19Middle<string>)).ShouldBeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(Row19Root)).ShouldBeFalse();
    }

    // ── row 20: [BsonId] on a BASE-declared member → also a no-op ─────────────────────

    /// <summary>
    /// Proves the walk honours <c>[BsonId]</c> at any level — which is what makes the first remedy
    /// named in the inherited-identity-member error actually work.
    /// </summary>
    [Fact]
    public void bson_id_on_a_base_declared_member_is_a_no_op()
    {
        MongoIdentityMapping.EnsureIdMember(typeof(Row20Derived));

        BsonClassMap.IsClassMapRegistered(typeof(Row20Derived)).ShouldBeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(Row20Base)).ShouldBeFalse();
    }

    // ── row 21: identity member inherited AND non-Id → precise throw ──────────────────

    /// <summary>
    /// The driver rejects <c>MapIdMember</c> for a member whose declaring type is not the class map's
    /// own class, and a class map can only be registered for one type — so this shape is beyond the
    /// mechanism's reach and must be refused with the remedies named, not silently skipped.
    /// </summary>
    [Fact]
    public void inherited_non_id_identity_member_throws_with_both_remedies()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => MongoIdentityMapping.EnsureIdMember(typeof(Row21Saga)));

        ex.Message.ShouldStartWith($"Wolverine resolved '{nameof(Row21Base.SagaId)}' as the identity member for");
        ex.Message.ShouldContain(nameof(Row21Saga));
        ex.Message.ShouldContain(nameof(Row21Base));           // names the declaring type
        ex.Message.ShouldContain("[BsonId]");                  // remedy 1
        ex.Message.ShouldContain("RegisterClassMap");           // remedy 2

        // Refusing must not leave a partial mapping behind.
        BsonClassMap.IsClassMapRegistered(typeof(Row21Saga)).ShouldBeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(Row21Base)).ShouldBeFalse();
    }

    // ── row 22: a different BASE-declared member already occupies _id → precise throw ──

    /// <summary>
    /// Wolverine's member is self-declared, so <c>MapIdMember</c> would succeed and
    /// <c>RegisterClassMap</c> would succeed — and then the type would throw
    /// <c>BsonSerializationException</c> on its <b>first write</b>, at runtime, inside the outbox
    /// transaction. Detecting it at codegen converts that into a host-build failure.
    /// </summary>
    [Fact]
    public void conflicting_base_declared_id_throws_without_recommending_bson_id()
    {
        var ex = Should.Throw<InvalidOperationException>(
            () => MongoIdentityMapping.EnsureIdMember(typeof(Row22Derived)));

        ex.Message.ShouldStartWith($"Wolverine resolved '{nameof(Row22Derived.Row22DerivedId)}' as the identity member for");
        ex.Message.ShouldContain(nameof(Row22Base));           // names where the conflict comes from
        ex.Message.ShouldContain("[BsonIgnore]");
        ex.Message.ShouldContain("[BsonElement(");

        // [BsonId] on the derived member produces the identical serialization failure, so the message
        // must not offer it as a remedy here.
        ex.Message.ShouldNotContain("[BsonId]");

        BsonClassMap.IsClassMapRegistered(typeof(Row22Derived)).ShouldBeFalse();
        BsonClassMap.IsClassMapRegistered(typeof(Row22Base)).ShouldBeFalse();
    }

    // ── row 11: idempotent after alignment ───────────────────────────────────────────

    [Fact]
    public void repeated_calls_after_alignment_are_no_ops()
    {
        MongoIdentityMapping.EnsureIdMember(typeof(HelperIdempotentDoc));
        MongoIdentityMapping.EnsureIdMember(typeof(HelperIdempotentDoc));
        MongoIdentityMapping.EnsureIdMember(
            typeof(HelperIdempotentDoc), MongoIdentityMapping.ResolveIdMember(typeof(HelperIdempotentDoc)));

        var map = BsonClassMap.LookupClassMap(typeof(HelperIdempotentDoc));
        map.IdMemberMap!.MemberName.ShouldBe(nameof(HelperIdempotentDoc.HelperIdempotentDocId));

        // The driver normalizes the id member's element name to _id at Freeze() — this is what lets
        // the frames' Eq("_id", …) filters keep working untouched.
        map.IsFrozen.ShouldBeTrue();
        map.IdMemberMap.ElementName.ShouldBe("_id");
    }
}

// ── one dedicated document type per fact (never shared, never reused) ───────────────

public class HelperPlainIdDoc
{
    public string Id { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public class HelperBsonIdDoc
{
    [BsonId] public string HelperBsonIdDocId { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public class HelperConflictDoc
{
    public Guid HelperConflictDocId { get; set; }
    public string Id { get; set; } = string.Empty;
}

public class HelperNoIdDoc
{
    public string Note { get; set; } = string.Empty;
}

public class HelperConcurrentDoc
{
    public Guid HelperConcurrentDocId { get; set; }
}

public class HelperIdempotentDoc
{
    public Guid HelperIdempotentDocId { get; set; }
}

// Row 19 — Id declared on a closed generic middle level, three deep, mirroring the upstream
// compliance shape (`LongBasicWorkflow : BasicWorkflow<LongStart, LongCompleteThree, long>`).
public class Row19Root
{
    public int Version { get; set; }
}

public class Row19Middle<TId> : Row19Root
{
    public TId Id { get; set; } = default!;
}

public class Row19Derived : Row19Middle<string>
{
    public string Name { get; set; } = string.Empty;
}

// Row 20 — [BsonId] on a base-declared member (also Wolverine's {TypeName}Id for the derived type).
public class Row20Base
{
    [BsonId] public string Row20DerivedId { get; set; } = string.Empty;
}

public class Row20Derived : Row20Base
{
    public string Note { get; set; } = string.Empty;
}

// Row 21 — a shared saga base contributing a non-Id identity member the driver will not map.
public abstract class Row21Base : Saga
{
    public Guid SagaId { get; set; }
}

public class Row21Saga : Row21Base
{
    public string Stage { get; set; } = string.Empty;
}

// Row 22 — the base declares Id (which the driver maps) while the type declares {TypeName}Id
// (which Wolverine's tier 2 prefers): two members competing for _id, across two types.
public class Row22Base
{
    public string Id { get; set; } = string.Empty;
}

public class Row22Derived : Row22Base
{
    public Guid Row22DerivedId { get; set; }
}
