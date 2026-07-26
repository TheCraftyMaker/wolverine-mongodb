using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// F7 — the <b>entity</b> half of identity agreement, plus the LD4 saga guards on the generic
/// storage-action paths.
///
/// <para><b>The read/write disagreement.</b> <c>LoadEntityFrame</c> filters <c>Eq("_id", …)</c> with
/// the value of the member <i>Wolverine</i> resolved (<c>SagaChain.DetermineSagaIdMember</c>:
/// <c>[SagaIdentity]</c> → <c>{TypeName}Id</c> → <c>{Name-minus-Saga}Id</c> → <c>SagaId</c> →
/// <c>Id</c>), while <c>UpsertAsync</c>/<c>DeleteAsync</c> key off whatever the <i>driver</i> mapped
/// (<c>BsonClassMap.LookupClassMap(…).IdMemberMap</c>: only <c>Id</c>/<c>id</c>/<c>_id</c>, plus
/// <c>[BsonId]</c>). Before this fix the two could name different members, so writes landed under one
/// key and reads probed another — a silent null on every load, or no mapped id member at all.
/// <c>MongoIdentityMapping.EnsureIdMember</c>, called from every entity frame constructor and from the
/// runtime collection accessor, makes them agree by construction; <c>IdOf</c> is left exactly as it
/// was because once the class map is aligned it <i>is</i> the Wolverine-resolved member.</para>
///
/// <para><b>LD4.</b> A non-saga handler returning <c>Delete&lt;TSaga&gt;</c> or
/// <c>IStorageAction&lt;TSaga&gt;</c> used to compile happily and then write to the un-prefixed
/// <i>entity</i> collection, unversioned, while the saga lived in <c>wolverine_saga_*</c>. Both paths
/// now throw at codegen.</para>
///
/// <para>Every scenario owns its document type outright: <c>BsonClassMap</c> registrations and
/// <c>MongoIdentityMapping</c>'s memo are both process-global and cannot be undone, so a type must
/// never be shared between two facts. Collection names resolve through
/// <see cref="MongoConstants.EntityCollectionName"/>, never a literal.</para>
/// </summary>
[Collection("mongodb")]
public class entity_identity_conventions
{
    private readonly AppFixture _fixture;
    public entity_identity_conventions(AppFixture fixture) => _fixture = fixture;

    private IMongoDatabase Database => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    /// <summary>
    /// Entity collections are application-owned, so <c>RebuildAsync</c> deliberately leaves them
    /// alone (D6 Decision 4c) — drop this file's explicitly. The first write inside a transaction
    /// recreates them.
    /// </summary>
    private async Task<IHost> BuildHostAsync(Type handlerType, params Type[] entityTypes)
    {
        var host = await PrepareHostAsync(handlerType, entityTypes);
        await host.StartAsync();
        return host;
    }

    /// <summary>
    /// The codegen guards fire while Wolverine compiles the handler graph, which happens during
    /// <c>StartAsync</c> (<c>HandlerGraph.Compile</c> → <c>SideEffectPolicy</c> → the return-value
    /// side effect's <c>BuildFrame</c>). So the failure surfaces at <b>host build</b> — LD4 §6 reason 4
    /// exactly — rather than lazily on the first message, and this helper asserts it there.
    /// </summary>
    private async Task<string> CodegenFailureTextAsync(Type handlerType, params Type[] entityTypes)
    {
        using var host = await PrepareHostAsync(handlerType, entityTypes);
        var ex = await Should.ThrowAsync<Exception>(() => host.StartAsync());
        return ex.ToString();
    }

    private async Task<IHost> PrepareHostAsync(Type handlerType, Type[] entityTypes)
    {
        await _fixture.ClearAll();
        foreach (var entityType in entityTypes)
        {
            await Database.DropCollectionAsync(MongoConstants.EntityCollectionName(entityType));
        }

        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Fresh compiled assembly per host (mirrors saga_identity_conventions / entity_atomicity).
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

                // Only the handler under test — never the rest of the assembly's handlers.
                opts.Discovery.DisableConventionalDiscovery().IncludeType(handlerType);

                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
                opts.Policies.AutoApplyTransactions();
            }).Build();
    }

    // Direct, Wolverine-independent read of the raw entity documents, so the assertions see the actual
    // on-disk shape: which member landed in _id, in what BSON type, and what else was written.
    private IMongoCollection<BsonDocument> RawDocuments(Type entityType)
        => Database.GetCollection<BsonDocument>(MongoConstants.EntityCollectionName(entityType));

    private async Task<BsonDocument> SingleDocumentAsync(Type entityType)
    {
        var documents = await RawDocuments(entityType).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

        // More than one document is the pre-fix failure mode: each write created a fresh
        // ObjectId-keyed orphan because the previous one could never be found.
        documents.Count.ShouldBe(1);
        return documents[0];
    }

    private Task<long> CountAsync(Type entityType)
        => RawDocuments(entityType).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty);

    // ── row 12: an entity keyed ONLY by {TypeName}Id ──────────────────────────────────

    /// <summary>
    /// The plain broken shape: nothing on <see cref="Crate"/> is named <c>Id</c>, so the driver maps
    /// no id member at all. Pre-fix, <c>Insert&lt;Crate&gt;</c> could not even name a key (<c>IdOf</c>
    /// throws "has no mapped _id member"), and a document written any other way carried a
    /// server-assigned <c>ObjectId</c> that the <c>[Entity]</c> load's <c>Eq("_id", crateId)</c> filter
    /// could never match. The full lifecycle — load → upsert → delete — must key one member.
    /// </summary>
    [Fact]
    public async Task type_name_id_convention_round_trips_through_load_insert_and_delete()
    {
        using var host = await BuildHostAsync(typeof(CrateHandler), typeof(Crate));
        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new RecordCrate(id, "glassware"));

        var document = await SingleDocumentAsync(typeof(Crate));
        document["_id"].BsonType.ShouldBe(BsonType.Binary);
        document["_id"].AsGuid.ShouldBe(id);
        document.Contains("CrateId").ShouldBeFalse();   // the id member is not duplicated
        document["Contents"].AsString.ShouldBe("glassware");

        // Update through a required [Entity] load: if the load and the write disagreed on the key the
        // entity would come back null and core would short-circuit the handler, leaving "glassware".
        await host.InvokeMessageAndWaitAsync(new RelabelCrate(id, "stemware"));
        var updated = await SingleDocumentAsync(typeof(Crate));
        updated["_id"].AsGuid.ShouldBe(id);
        updated["Contents"].AsString.ShouldBe("stemware");

        // Delete<Crate> keys _id off the class map; it must find the same document.
        await host.InvokeMessageAndWaitAsync(new DiscardCrate(id));
        (await CountAsync(typeof(Crate))).ShouldBe(0);
    }

    // ── row 13: the poisoned shape — BOTH {TypeName}Id and Id ─────────────────────────

    /// <summary>
    /// The review's shape. Wolverine resolves <c>LedgerId</c> ({TypeName}Id outranks Id); the driver
    /// resolves <c>Id</c>. Pre-fix the two disagreed silently, and the app only got away with it while
    /// it kept both members equal. After alignment <c>LedgerId</c> is the <c>_id</c> and <c>Id</c>
    /// demotes to an ordinary field — deliberately given a <i>different</i> value here so the
    /// assertion can tell which member the read and the write each used.
    /// </summary>
    [Fact]
    public async Task entity_with_both_type_name_id_and_id_reads_and_writes_the_same_key()
    {
        using var host = await BuildHostAsync(typeof(LedgerHandler), typeof(Ledger));
        var id = Guid.NewGuid();
        var code = "LEDGER-" + Guid.NewGuid().ToString("N");

        await host.InvokeMessageAndWaitAsync(new OpenLedger(id, code, "opening balance"));

        var document = await SingleDocumentAsync(typeof(Ledger));
        document["_id"].BsonType.ShouldBe(BsonType.Binary);
        document["_id"].AsGuid.ShouldBe(id);
        document.Contains("LedgerId").ShouldBeFalse();
        document["Id"].AsString.ShouldBe(code);         // the demoted member survives as a plain field
        document["Memo"].AsString.ShouldBe("opening balance");

        await host.InvokeMessageAndWaitAsync(new AnnotateLedger(id, "reconciled"));
        var updated = await SingleDocumentAsync(typeof(Ledger));
        updated["_id"].AsGuid.ShouldBe(id);
        updated["Id"].AsString.ShouldBe(code);
        updated["Memo"].AsString.ShouldBe("reconciled");

        await host.InvokeMessageAndWaitAsync(new CloseLedger(id));
        (await CountAsync(typeof(Ledger))).ShouldBe(0);
    }

    // ── row 24(a): an inherited Id member — the no-op path must still agree ───────────

    /// <summary>
    /// <see cref="Receipt"/> inherits <c>Id</c> from <see cref="ArchiveBase"/>. The driver already maps
    /// it (its own map inherits the base's id member), so the alignment helper does nothing and the
    /// BSON registry is left untouched — the byte-identical path every working consumer takes. This
    /// fact exists to prove the load filter and <c>IdOf</c>'s <c>LookupClassMap</c> agree on an
    /// <i>inherited</i> member, which a single unfrozen <c>AutoMap</c> of the subclass would not report.
    /// </summary>
    [Fact]
    public async Task inherited_id_member_round_trips_with_the_registry_untouched()
    {
        using var host = await BuildHostAsync(typeof(ReceiptHandler), typeof(Receipt));
        var id = "RECEIPT-" + Guid.NewGuid().ToString("N");

        await host.InvokeMessageAndWaitAsync(new FileReceipt(id, "coffee"));

        var document = await SingleDocumentAsync(typeof(Receipt));
        document["_id"].BsonType.ShouldBe(BsonType.String);
        document["_id"].AsString.ShouldBe(id);
        document.Contains("Id").ShouldBeFalse();
        document["Note"].AsString.ShouldBe("coffee");

        // The driver resolved the inherited member on its own; we registered nothing of our own for
        // either level beyond the map the driver itself creates and freezes on first use.
        BsonClassMap.LookupClassMap(typeof(Receipt))
            .IdMemberMap!.MemberName.ShouldBe(nameof(ArchiveBase.Id));

        await host.InvokeMessageAndWaitAsync(new ShredReceipt(id));
        (await CountAsync(typeof(Receipt))).ShouldBe(0);
    }

    // ── row 24(b): an inherited NON-Id identity member fails at codegen ───────────────

    /// <summary>
    /// <see cref="Beacon"/>'s identity member is declared on <see cref="BeaconBase"/>, and the driver
    /// will not let a subclass's class map claim a member it does not own. Beyond the mechanism's
    /// reach, so it must be refused at codegen with its two verified remedies named — not written as
    /// an <c>ObjectId</c>-keyed orphan on the first message.
    /// </summary>
    [Fact]
    public async Task entity_with_inherited_non_id_identity_member_fails_at_codegen()
    {
        // Codegen wraps compilation failures, so assert on the surfaced text, not the outer type.
        var text = await CodegenFailureTextAsync(typeof(BeaconHandler), typeof(Beacon));

        text.ShouldContain($"Wolverine resolved '{nameof(BeaconBase.Serial)}' as the identity member for");
        text.ShouldContain(nameof(Beacon));
        text.ShouldContain(nameof(BeaconBase));
        text.ShouldContain("[BsonId]");

        (await CountAsync(typeof(Beacon))).ShouldBe(0);
    }

    // ── row 24(c): a base-declared member already squatting on _id fails at codegen ───

    /// <summary>
    /// <see cref="Vault"/> declares <c>VaultId</c> (which Wolverine resolves) while
    /// <see cref="VaultBase"/> contributes an inherited <c>Id</c> the driver has already claimed as
    /// <c>_id</c>. Mapping ours registers cleanly and then throws <c>BsonSerializationException</c> on
    /// the first write, inside the outbox transaction — so refuse at codegen instead. The message must
    /// <b>not</b> recommend <c>[BsonId]</c>: on this shape it produces the identical failure.
    /// </summary>
    [Fact]
    public async Task entity_with_conflicting_inherited_id_fails_at_codegen()
    {
        var text = await CodegenFailureTextAsync(typeof(VaultHandler), typeof(Vault));

        text.ShouldContain($"Wolverine resolved '{nameof(Vault.VaultId)}' as the identity member for");
        text.ShouldContain(nameof(VaultBase));
        text.ShouldContain("[BsonIgnore]");

        (await CountAsync(typeof(Vault))).ShouldBe(0);
    }

    // ── rows 15/16: LD4 — saga types are rejected on the storage-action paths ─────────

    /// <summary>
    /// Row 15. A plain handler returning <c>Delete&lt;TSaga&gt;</c> reaches the generic single-variable
    /// <c>DetermineDeleteFrame</c>, which is the <b>entity</b> path: pre-fix it silently deleted from
    /// the un-prefixed <c>relicsaga</c> collection while the saga lives in
    /// <c>wolverine_saga_relicsaga</c> — a write that appears to succeed and affects nothing the saga
    /// machinery reads. Routing it to the saga frames was rejected (no <c>SagaChain</c> ⇒ no captured
    /// <c>oldVersion</c> ⇒ unguarded writes into saga collections), so it throws at codegen.
    /// </summary>
    [Fact]
    public async Task delete_of_a_saga_from_a_plain_handler_fails_at_codegen()
    {
        var text = await CodegenFailureTextAsync(typeof(RelicDeleteHandler), typeof(RelicSaga));

        text.ShouldContain("Cannot use Delete<");
        text.ShouldContain("from a non-saga handler:");
        text.ShouldContain(nameof(RelicSaga));
        text.ShouldContain("MarkCompleted()");

        // Nothing was written to the entity-named collection the pre-fix frame targeted.
        (await CountAsync(typeof(RelicSaga))).ShouldBe(0);
    }

    /// <summary>
    /// Row 16. The same misuse through the <c>IStorageAction&lt;TSaga&gt;</c> return-value path, which
    /// builds a bare <c>MethodCall</c> rather than a frame — so the guard lives in
    /// <c>DetermineStorageActionFrame</c> itself.
    /// </summary>
    [Fact]
    public async Task storage_action_of_a_saga_from_a_plain_handler_fails_at_codegen()
    {
        var text = await CodegenFailureTextAsync(typeof(RelicStorageActionHandler), typeof(RelicSaga));

        text.ShouldContain("Cannot use IStorageAction<");
        text.ShouldContain("from a non-saga handler:");
        text.ShouldContain(nameof(RelicSaga));
        text.ShouldContain("MarkCompleted()");

        (await CountAsync(typeof(RelicSaga))).ShouldBe(0);
    }

    // ── row 17: the runtime leg (TypeLoadMode.Static safety net) ──────────────────────

    /// <summary>
    /// Design §4.1: in <c>TypeLoadMode.Static</c> the generated types are attached without ever calling
    /// <c>HandlerChain.AssembleTypes</c>, so <b>no frame constructor runs</b> — codegen-time alignment
    /// alone would be silently absent from a pre-generated deployment. Alignment therefore also happens
    /// in <see cref="MongoEntityOperations"/>'s collection accessor. <see cref="Pallet"/> is referenced
    /// by no handler and no host in this assembly, so its frames are never constructed, yet a raw
    /// upsert/load/delete still keys the document by the Wolverine-resolved identity member.
    /// </summary>
    [Fact]
    public async Task operations_align_identity_without_any_frame_ever_being_constructed()
    {
        var id = Guid.NewGuid();
        using var session = await _fixture.Client.StartSessionAsync();

        await MongoEntityOperations.UpsertAsync(
            Database, session, new Pallet { PalletId = id, Contents = "bricks" }, CancellationToken.None);

        var document = await RawDocuments(typeof(Pallet))
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id)).SingleAsync();
        document["_id"].AsGuid.ShouldBe(id);
        document.Contains("PalletId").ShouldBeFalse();

        var loaded = await MongoEntityOperations.LoadAsync<Pallet, Guid>(
            Database, session, id, CancellationToken.None);
        loaded.ShouldNotBeNull();
        loaded.Contents.ShouldBe("bricks");

        await MongoEntityOperations.DeleteAsync(Database, session, loaded, CancellationToken.None);
        (await RawDocuments(typeof(Pallet))
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", id))).ShouldBe(0);
    }
}

// ── one dedicated entity type per scenario ──────────────────────────────────────────
//
// Class maps and the alignment memo are process-global (design §9.1), so these types are used by
// exactly one fact each and by nothing else in the assembly.

/// <summary>Keyed only by <c>{TypeName}Id</c> — the driver maps no id member of its own.</summary>
public class Crate
{
    public Guid CrateId { get; set; }
    public string? Contents { get; set; }
}

public record RecordCrate(Guid CrateId, string Contents);
public record RelabelCrate(Guid CrateId, string Contents);
public record DiscardCrate(Guid CrateId);

public static class CrateHandler
{
    public static Insert<Crate> Handle(RecordCrate msg)
        => Storage.Insert(new Crate { CrateId = msg.CrateId, Contents = msg.Contents });

    public static Update<Crate> Handle(RelabelCrate msg, [Entity] Crate crate)
    {
        crate.Contents = msg.Contents;
        return Storage.Update(crate);
    }

    public static Delete<Crate> Handle(DiscardCrate msg, [Entity] Crate crate) => Storage.Delete(crate);
}

/// <summary>
/// The review's poisoned shape: Wolverine resolves <c>LedgerId</c>, the driver resolves <c>Id</c>.
/// </summary>
public class Ledger
{
    public Guid LedgerId { get; set; }
    public string Id { get; set; } = string.Empty;
    public string? Memo { get; set; }
}

public record OpenLedger(Guid LedgerId, string Code, string Memo);
public record AnnotateLedger(Guid LedgerId, string Memo);
public record CloseLedger(Guid LedgerId);

public static class LedgerHandler
{
    public static Insert<Ledger> Handle(OpenLedger msg)
        => Storage.Insert(new Ledger { LedgerId = msg.LedgerId, Id = msg.Code, Memo = msg.Memo });

    public static Update<Ledger> Handle(AnnotateLedger msg, [Entity] Ledger ledger)
    {
        ledger.Memo = msg.Memo;
        return Storage.Update(ledger);
    }

    public static Delete<Ledger> Handle(CloseLedger msg, [Entity] Ledger ledger) => Storage.Delete(ledger);
}

/// <summary>A shared base contributing an <c>Id</c> the driver maps on its own.</summary>
public abstract class ArchiveBase
{
    public string Id { get; set; } = string.Empty;
}

public class Receipt : ArchiveBase
{
    public string? Note { get; set; }
}

public record FileReceipt(string Id, string Note);
public record ShredReceipt(string Id);

public static class ReceiptHandler
{
    public static Insert<Receipt> Handle(FileReceipt msg)
        => Storage.Insert(new Receipt { Id = msg.Id, Note = msg.Note });

    public static Delete<Receipt> Handle(ShredReceipt msg, [Entity] Receipt receipt) => Storage.Delete(receipt);
}

/// <summary>
/// A shared base contributing a non-<c>Id</c> identity member — the shape the driver cannot map from
/// the subclass's own class map.
/// </summary>
public abstract class BeaconBase
{
    [SagaIdentity] public Guid Serial { get; set; }
}

public class Beacon : BeaconBase
{
    public string? Status { get; set; }
}

public record LightBeacon(Guid Serial);

// Deliberately mis-shaped: every fact below asserts that this handler FAILS at host build. It must
// therefore be invisible to conventional discovery — otherwise it would break every other suite in
// this assembly whose host scans it. [WolverineIgnore] excludes it from the scan; the explicit
// IncludeType in this file's host builder still registers it (HandlerDiscovery.FindCalls
// concatenates the explicit types AFTER the query).
[WolverineIgnore]
public static class BeaconHandler
{
    public static Insert<Beacon> Handle(LightBeacon msg)
        => Storage.Insert(new Beacon { Serial = msg.Serial, Status = "lit" });
}

/// <summary>A shared base whose inherited <c>Id</c> already occupies <c>_id</c>.</summary>
public abstract class VaultBase
{
    public string Id { get; set; } = string.Empty;
}

public class Vault : VaultBase
{
    public Guid VaultId { get; set; }
    public string? Contents { get; set; }
}

public record SealVault(Guid VaultId, string Contents);

// Deliberately mis-shaped: every fact below asserts that this handler FAILS at host build. It must
// therefore be invisible to conventional discovery — otherwise it would break every other suite in
// this assembly whose host scans it. [WolverineIgnore] excludes it from the scan; the explicit
// IncludeType in this file's host builder still registers it (HandlerDiscovery.FindCalls
// concatenates the explicit types AFTER the query).
[WolverineIgnore]
public static class VaultHandler
{
    public static Insert<Vault> Handle(SealVault msg)
        => Storage.Insert(new Vault { VaultId = msg.VaultId, Contents = msg.Contents });
}

/// <summary>
/// A saga used <b>only</b> as the illegal operand of the two storage-action guards — never started,
/// never handled, never persisted.
/// </summary>
public class RelicSaga : Saga
{
    public Guid Id { get; set; }
}

public record PurgeRelic(Guid Id);
public record ArchiveRelic(Guid Id);

// Deliberately mis-shaped: every fact below asserts that this handler FAILS at host build. It must
// therefore be invisible to conventional discovery — otherwise it would break every other suite in
// this assembly whose host scans it. [WolverineIgnore] excludes it from the scan; the explicit
// IncludeType in this file's host builder still registers it (HandlerDiscovery.FindCalls
// concatenates the explicit types AFTER the query).
[WolverineIgnore]
public static class RelicDeleteHandler
{
    public static Delete<RelicSaga> Handle(PurgeRelic msg) => Storage.Delete(new RelicSaga { Id = msg.Id });
}

// Deliberately mis-shaped: every fact below asserts that this handler FAILS at host build. It must
// therefore be invisible to conventional discovery — otherwise it would break every other suite in
// this assembly whose host scans it. [WolverineIgnore] excludes it from the scan; the explicit
// IncludeType in this file's host builder still registers it (HandlerDiscovery.FindCalls
// concatenates the explicit types AFTER the query).
[WolverineIgnore]
public static class RelicStorageActionHandler
{
    public static IStorageAction<RelicSaga> Handle(ArchiveRelic msg)
        => Storage.Delete(new RelicSaga { Id = msg.Id });
}

/// <summary>
/// Keyed by <c>{TypeName}Id</c> and used <b>only</b> by the runtime-leg fact. Deliberately
/// handler-free so no frame is ever constructed for it anywhere in this assembly.
/// </summary>
public class Pallet
{
    public Guid PalletId { get; set; }
    public string? Contents { get; set; }
}
