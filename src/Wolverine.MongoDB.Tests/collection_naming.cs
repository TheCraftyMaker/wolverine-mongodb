using Shouldly;
using Wolverine.Attributes;
using Wolverine.MongoDB.Internals;
using Xunit;

namespace Wolverine.MongoDB.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="MongoCollectionNaming"/> — the resolution point that layers explicit
    /// per-type mappings and collision detection over <see cref="MongoConstants"/>' default naming.
    /// Every fact here is a pure function of <see cref="Type"/>, so none of them needs Docker, a
    /// MongoDB connection or a Wolverine host.
    ///
    /// <para><b>Isolation contract.</b> The mapping and claim registries are process-global and cannot be
    /// reset (the same constraint <c>identity_mapping_helper</c> documents for <c>BsonClassMap</c>). Both
    /// are keyed by <b>database name</b>, so every fact below uses its own synthetic database name — a
    /// name no live fixture uses — and its own dedicated document types. That makes the facts independent
    /// of each other, of execution order, and of the rest of the assembly.</para>
    /// </summary>
    public class collection_naming
    {
        // ── the migration-safety pins: defaults must never change ─────────────────────

        /// <summary>
        /// The default saga name is frozen. Until now the only regression guard on it was a literal in
        /// the demo solution (<c>SagaFlowTests</c>'s <c>"wolverine_saga_orderfulfillmentsaga"</c>); this
        /// puts the guard in <c>src/</c>, so a future refactor cannot silently rename a live collection.
        /// </summary>
        [Fact]
        public void default_saga_collection_name_is_unchanged()
        {
            MongoConstants.SagaCollectionName(typeof(Naming.OrderFulfillmentSaga))
                .ShouldBe("wolverine_saga_orderfulfillmentsaga");

            MongoCollectionNaming.ResolveSaga("naming_defaults", typeof(Naming.OrderFulfillmentSaga))
                .ShouldBe("wolverine_saga_orderfulfillmentsaga");
        }

        /// <summary>
        /// The default entity name is frozen — un-prefixed and lowercased, matching the demo's
        /// <c>"ordernote"</c> literal.
        /// </summary>
        [Fact]
        public void default_entity_collection_name_is_unchanged()
        {
            MongoConstants.EntityCollectionName(typeof(Naming.OrderNote)).ShouldBe("ordernote");

            MongoCollectionNaming.ResolveEntity("naming_defaults", typeof(Naming.OrderNote))
                .ShouldBe("ordernote");
        }

        /// <summary>
        /// Closed generics and nested types keep the names they have today. This is the deliberate
        /// rejection of Wolverine's own RDBMS alias sanitizer as a default: it disambiguates both axes,
        /// but it would rename every generic and nested persisted type — including ones with no conflict
        /// at all.
        /// </summary>
        [Fact]
        public void generic_and_nested_default_names_are_unchanged()
        {
            MongoConstants.EntityCollectionName(typeof(Naming.Box<int>)).ShouldBe("box`1");
            MongoConstants.EntityCollectionName(typeof(Naming.Outer.Inner)).ShouldBe("inner");

            MongoCollectionNaming.ResolveEntity("naming_defaults_generic", typeof(Naming.Box<int>))
                .ShouldBe("box`1");
        }

        // ── the three collision axes ──────────────────────────────────────────────────

        /// <summary>
        /// The realistic case: two entity types with the same simple name in different namespaces. The
        /// message must name both types in full, the shared collection, the database, and the exact
        /// mapping call — an operator hitting this at deploy time has nothing else to go on.
        /// </summary>
        [Fact]
        public void namespace_collision_throws_naming_both_types()
        {
            const string database = "naming_namespace_collision";

            MongoCollectionNaming.ClaimEntity(database, typeof(NamingLeft.Duplicate)).ShouldBe("duplicate");

            var ex = Should.Throw<InvalidOperationException>(
                () => MongoCollectionNaming.ClaimEntity(database, typeof(NamingRight.Duplicate)));

            ex.Message.ShouldContain("Wolverine.MongoDB.Tests.NamingLeft.Duplicate");
            ex.Message.ShouldContain("Wolverine.MongoDB.Tests.NamingRight.Duplicate");
            ex.Message.ShouldContain("'duplicate'");
            ex.Message.ShouldContain(database);
            ex.Message.ShouldContain("MapEntityCollection");
        }

        /// <summary>
        /// Two types differing only by case. Legal C#, and it is the <c>ToLowerInvariant()</c> in
        /// <see cref="MongoConstants.EntityCollectionName"/> that manufactures the collision — MongoDB
        /// collection names are themselves case-sensitive.
        /// </summary>
        [Fact]
        public void case_collision_throws()
        {
            const string database = "naming_case_collision";

            MongoCollectionNaming.ClaimEntity(database, typeof(Naming.NamingMetric)).ShouldBe("namingmetric");

            Should.Throw<InvalidOperationException>(
                    () => MongoCollectionNaming.ClaimEntity(database, typeof(Naming.NAMINGMETRIC)))
                .Message.ShouldContain("'namingmetric'");
        }

        /// <summary>
        /// Two closed constructions of one open generic. <c>Type.Name</c> is <c>Box`1</c> for both, so
        /// they share a collection unless one is mapped.
        /// </summary>
        [Fact]
        public void closed_generic_constructions_collide()
        {
            const string database = "naming_generic_collision";

            MongoCollectionNaming.ClaimEntity(database, typeof(Naming.Box<int>)).ShouldBe("box`1");

            var ex = Should.Throw<InvalidOperationException>(
                () => MongoCollectionNaming.ClaimEntity(database, typeof(Naming.Box<string>)));

            ex.Message.ShouldContain("Box<int>");
            ex.Message.ShouldContain("Box<string>");
        }

        /// <summary>
        /// The saga axis, which is worse than the entity one: a shared saga collection also corrupts the
        /// <c>Saga.Version</c> guard. The remedy named must be <c>MapSagaCollection</c>.
        /// </summary>
        [Fact]
        public void saga_collision_throws_naming_the_saga_mapping_call()
        {
            const string database = "naming_saga_collision";

            MongoCollectionNaming.ClaimSaga(database, typeof(NamingLeft.DuplicateSaga))
                .ShouldBe("wolverine_saga_duplicatesaga");

            var ex = Should.Throw<InvalidOperationException>(
                () => MongoCollectionNaming.ClaimSaga(database, typeof(NamingRight.DuplicateSaga)));

            ex.Message.ShouldContain("Wolverine.MongoDB.Tests.NamingLeft.DuplicateSaga");
            ex.Message.ShouldContain("Wolverine.MongoDB.Tests.NamingRight.DuplicateSaga");
            ex.Message.ShouldContain("MapSagaCollection");
            ex.Message.ShouldContain(MongoConstants.SagaCollectionPrefix);
        }

        /// <summary>
        /// The cross-category claim, verified rather than assumed: the <c>wolverine_saga_</c> prefix
        /// keeps a saga and an entity of the same simple name apart, so that pairing is not a collision.
        /// </summary>
        [Fact]
        public void a_saga_and_an_entity_with_the_same_simple_name_do_not_collide()
        {
            const string database = "naming_cross_category";

            MongoCollectionNaming.ClaimSaga(database, typeof(NamingLeft.TicketSaga))
                .ShouldBe("wolverine_saga_ticketsaga");
            MongoCollectionNaming.ClaimEntity(database, typeof(NamingRight.TicketSaga))
                .ShouldBe("ticketsaga");
        }

        /// <summary>
        /// Claims are scoped to a database, so two hosts in one process pointed at different databases
        /// never false-positive on each other.
        /// </summary>
        [Fact]
        public void the_same_name_in_two_databases_is_not_a_collision()
        {
            MongoCollectionNaming.ClaimEntity("naming_db_one", typeof(NamingLeft.Divided)).ShouldBe("divided");
            MongoCollectionNaming.ClaimEntity("naming_db_two", typeof(NamingRight.Divided)).ShouldBe("divided");
        }

        /// <summary>
        /// A type re-claiming the collection it already owns is a silent no-op, and so is re-applying an
        /// identical mapping. This is what makes repeated and concurrent host construction safe — the
        /// compliance suites build many hosts against one database, and the multinode fixtures build two
        /// at once.
        /// </summary>
        [Fact]
        public void repeated_claims_and_repeated_mappings_are_no_ops()
        {
            const string database = "naming_idempotent";
            var mappings = new Dictionary<Type, MongoCollectionMapping>
            {
                [typeof(Naming.Repeatable)] = new("naming_repeatable", IsSaga: false)
            };

            MongoCollectionNaming.ApplyMappings(database, mappings);
            MongoCollectionNaming.ApplyMappings(database, mappings);

            MongoCollectionNaming.ClaimEntity(database, typeof(Naming.Repeatable)).ShouldBe("naming_repeatable");
            MongoCollectionNaming.ClaimEntity(database, typeof(Naming.Repeatable)).ShouldBe("naming_repeatable");
        }

        // ── the escape hatch, and its own failure modes ───────────────────────────────

        /// <summary>
        /// An explicit mapping resolves the collision: both types then claim cleanly, under distinct
        /// names, and the unmapped one is untouched.
        /// </summary>
        [Fact]
        public void an_explicit_mapping_resolves_the_collision()
        {
            const string database = "naming_mapping_resolves";

            MongoCollectionNaming.ApplyMappings(database, new Dictionary<Type, MongoCollectionMapping>
            {
                [typeof(NamingRight.Mapped)] = new("naming_right_mapped", IsSaga: false)
            });

            MongoCollectionNaming.ClaimEntity(database, typeof(NamingLeft.Mapped)).ShouldBe("mapped");
            MongoCollectionNaming.ClaimEntity(database, typeof(NamingRight.Mapped)).ShouldBe("naming_right_mapped");
        }

        /// <summary>
        /// Two types given the <i>same</i> explicit name must not quietly recreate the bug the mapping
        /// API exists to fix. Caught at configuration time by the options object.
        /// </summary>
        [Fact]
        public void two_types_cannot_be_mapped_to_the_same_collection()
        {
            var options = new MongoDbPersistenceOptions()
                .MapEntityCollection<NamingLeft.Doubled>("naming_doubled");

            Should.Throw<ArgumentException>(
                () => options.MapEntityCollection<NamingRight.Doubled>("naming_doubled"));
        }

        /// <summary>
        /// A saga mapping must keep the <c>wolverine_saga_</c> prefix. Without that constraint an
        /// explicitly mapped saga would silently drop out of
        /// <c>IMessageStoreAdmin.ClearAllAsync</c>/<c>RebuildAsync</c>'s prefix sweep.
        /// </summary>
        [Fact]
        public void a_saga_mapping_must_stay_inside_the_saga_prefix()
        {
            var options = new MongoDbPersistenceOptions();

            Should.Throw<ArgumentException>(
                    () => options.MapSagaCollection<NamingLeft.PrefixSaga>("prefixsaga"))
                .Message.ShouldContain(MongoConstants.SagaCollectionPrefix);

            options.MapSagaCollection<NamingLeft.PrefixSaga>("wolverine_saga_left_prefixsaga");
            options.CollectionMappings[typeof(NamingLeft.PrefixSaga)].CollectionName
                .ShouldBe("wolverine_saga_left_prefixsaga");
        }

        /// <summary>
        /// The mirror-image rule: an entity mapping may not take a saga-prefixed name (every
        /// <c>RebuildAsync</c> would drop that application data) nor one of the nine Wolverine system
        /// collections (its documents would interleave with the inbox/outbox/node state).
        /// </summary>
        [Fact]
        public void an_entity_mapping_may_not_take_a_saga_prefixed_or_reserved_name()
        {
            var options = new MongoDbPersistenceOptions();

            Should.Throw<ArgumentException>(
                () => options.MapEntityCollection<NamingLeft.Reserved>("wolverine_saga_reserved"));

            Should.Throw<ArgumentException>(
                () => options.MapEntityCollection<NamingLeft.Reserved>(MongoConstants.NodeCollection));

            Should.Throw<ArgumentException>(
                () => options.MapEntityCollection<NamingLeft.Reserved>("   "));

            Should.Throw<ArgumentException>(
                () => options.MapEntityCollection<NamingLeft.Reserved>("bad$name"));
        }

        /// <summary>
        /// The present-tense hazard, now refused instead of silently destructive: an entity type whose
        /// <i>default</i> name lands inside the saga prefix would have its documents dropped by every
        /// administrative rebuild, and one that lands on a system collection would interleave with
        /// Wolverine's own state.
        /// </summary>
        [Fact]
        public void an_entity_whose_default_name_is_reserved_is_refused()
        {
            const string database = "naming_reserved_default";

            Should.Throw<InvalidOperationException>(
                    () => MongoCollectionNaming.ClaimEntity(database, typeof(Naming.Wolverine_saga_Trap)))
                .Message.ShouldContain("MapEntityCollection");

            Should.Throw<InvalidOperationException>(
                    () => MongoCollectionNaming.ClaimEntity(database, typeof(Naming.Wolverine_nodes)))
                .Message.ShouldContain("MapEntityCollection");
        }

        /// <summary>
        /// Two hosts in one process, same database, disagreeing about where a type lives. Collection
        /// naming is a property of the (process, database) pair — a disagreement is a configuration bug
        /// and is made loud rather than resolved by whichever host happened to start first.
        /// </summary>
        [Fact]
        public void a_conflicting_mapping_for_an_already_named_type_throws()
        {
            const string database = "naming_conflicting_mapping";

            MongoCollectionNaming.ApplyMappings(database, new Dictionary<Type, MongoCollectionMapping>
            {
                [typeof(Naming.Contested)] = new("naming_contested_first", IsSaga: false)
            });

            Should.Throw<InvalidOperationException>(() =>
                MongoCollectionNaming.ApplyMappings(database, new Dictionary<Type, MongoCollectionMapping>
                {
                    [typeof(Naming.Contested)] = new("naming_contested_second", IsSaga: false)
                }));
        }

        /// <summary>
        /// A mapping applied after the type has already been resolved to its default in the same
        /// database also throws, rather than leaving two hosts writing to two collections.
        /// </summary>
        [Fact]
        public void a_mapping_applied_after_the_default_was_already_used_throws()
        {
            const string database = "naming_late_mapping";

            MongoCollectionNaming.ClaimEntity(database, typeof(Naming.LateMapped)).ShouldBe("latemapped");

            Should.Throw<InvalidOperationException>(() =>
                MongoCollectionNaming.ApplyMappings(database, new Dictionary<Type, MongoCollectionMapping>
                {
                    [typeof(Naming.LateMapped)] = new("naming_late_mapped", IsSaga: false)
                }));
        }

        /// <summary>
        /// The <c>TypeLoadMode.Static</c> leg as a deterministic proxy: the collection accessors
        /// (<c>MongoSagaOperations</c>/<c>MongoEntityOperations</c>) take the same claim, so a collision
        /// is still caught when no frame constructor ever runs. The real Static-mode coverage is the
        /// handler policy, which runs inside <c>HandlerGraph.Compile</c> regardless of
        /// <c>TypeLoadMode</c>.
        /// </summary>
        [Fact]
        public void the_runtime_accessor_path_detects_a_collision_with_no_frame_involved()
        {
            const string database = "naming_runtime_leg";

            MongoCollectionNaming.ClaimEntity(database, typeof(NamingLeft.Runtime)).ShouldBe("runtime");

            Should.Throw<InvalidOperationException>(
                () => MongoCollectionNaming.ClaimEntity(database, typeof(NamingRight.Runtime)));
        }

        /// <summary>
        /// <c>MapSagaCollection</c> and <c>MapEntityCollection</c> are not interchangeable — using the
        /// wrong one is a configuration mistake with real consequences for the admin sweep, so it is
        /// refused rather than coerced.
        /// </summary>
        [Fact]
        public void the_mapping_methods_reject_the_wrong_kind_of_type()
        {
            var options = new MongoDbPersistenceOptions();

            Should.Throw<ArgumentException>(
                () => options.MapSagaCollection(typeof(Naming.OrderNote), "wolverine_saga_ordernote"));

            Should.Throw<ArgumentException>(
                () => options.MapEntityCollection(typeof(Naming.OrderFulfillmentSaga), "ordersaga"));
        }
    }
}

// ── fixture types ─────────────────────────────────────────────────────────────────────
// None of these is referenced anywhere else in the assembly, and none has a handler, so nothing here
// is ever discovered, persisted, or shared with another fact.

namespace Wolverine.MongoDB.Tests.Naming
{
    /// <summary>Pins the default saga name against the demo's literal.</summary>
    [WolverineIgnore]
    public class OrderFulfillmentSaga : Saga
    {
        /// <summary>Saga identity.</summary>
        public Guid Id { get; set; }
    }

    /// <summary>Pins the default entity name against the demo's literal.</summary>
    public class OrderNote
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>An open generic: every closed construction shares the name <c>Box`1</c>.</summary>
    /// <typeparam name="T">Payload type.</typeparam>
    public class Box<T>
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Payload.</summary>
        public T? Contents { get; set; }
    }

    /// <summary>Declaring type for the nested-name pin.</summary>
    public static class Outer
    {
        /// <summary>A nested type: <c>Type.Name</c> drops the declaring type.</summary>
        public class Inner
        {
            /// <summary>Document identity.</summary>
            public string Id { get; set; } = string.Empty;
        }
    }

    /// <summary>Half of the case-collision pair.</summary>
    public class NamingMetric
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>The other half of the case-collision pair — legal C#, same lowercased name.</summary>
    public class NAMINGMETRIC
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Used by the idempotency fact.</summary>
    public class Repeatable
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Used by the conflicting-mapping fact.</summary>
    public class Contested
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Used by the mapping-after-default fact.</summary>
    public class LateMapped
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>An entity whose default name lands inside the saga prefix that rebuilds sweep.</summary>
    public class Wolverine_saga_Trap
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>An entity whose default name is a Wolverine system collection.</summary>
    public class Wolverine_nodes
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }
}

namespace Wolverine.MongoDB.Tests.NamingLeft
{
    /// <summary>First half of the namespace-collision pair.</summary>
    public class Duplicate
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>First half of the saga namespace-collision pair.</summary>
    [WolverineIgnore]
    public class DuplicateSaga : Saga
    {
        /// <summary>Saga identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>The saga half of the cross-category pair.</summary>
    [WolverineIgnore]
    public class TicketSaga : Saga
    {
        /// <summary>Saga identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>First half of the two-databases pair.</summary>
    public class Divided
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>First half of the mapping-resolves pair.</summary>
    public class Mapped
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>First half of the same-mapped-name pair.</summary>
    public class Doubled
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Used by the saga-prefix validation fact.</summary>
    [WolverineIgnore]
    public class PrefixSaga : Saga
    {
        /// <summary>Saga identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Used by the reserved-name validation fact.</summary>
    public class Reserved
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>First half of the runtime-leg pair.</summary>
    public class Runtime
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }
}

namespace Wolverine.MongoDB.Tests.NamingRight
{
    /// <summary>Second half of the namespace-collision pair.</summary>
    public class Duplicate
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Second half of the saga namespace-collision pair.</summary>
    [WolverineIgnore]
    public class DuplicateSaga : Saga
    {
        /// <summary>Saga identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>The entity half of the cross-category pair — same simple name as a saga.</summary>
    public class TicketSaga
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Second half of the two-databases pair.</summary>
    public class Divided
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Second half of the mapping-resolves pair.</summary>
    public class Mapped
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Second half of the same-mapped-name pair.</summary>
    public class Doubled
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }

    /// <summary>Second half of the runtime-leg pair.</summary>
    public class Runtime
    {
        /// <summary>Document identity.</summary>
        public string Id { get; set; } = string.Empty;
    }
}
