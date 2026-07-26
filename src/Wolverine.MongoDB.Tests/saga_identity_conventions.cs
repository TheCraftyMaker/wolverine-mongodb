using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using MongoDB.Driver;
using Shouldly;
using Wolverine.MongoDB.Internals;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.MongoDB.Tests;

#pragma warning disable CS8981

/// <summary>
/// F6 — every legal Wolverine saga identity convention round-trips through MongoDB.
///
/// <para>Wolverine resolves a saga's identity member by <i>its</i> convention
/// (<c>SagaChain.DetermineSagaIdMember</c>: <c>[SagaIdentity]</c> → <c>{TypeName}Id</c> →
/// <c>{Name-minus-Saga}Id</c> → <c>SagaId</c> → <c>Id</c>); the MongoDB driver resolves <c>_id</c> by
/// its own (<c>NamedIdMemberConvention</c>: only <c>Id</c>/<c>id</c>/<c>_id</c>, plus
/// <c>[BsonId]</c>). Before this fix nothing reconciled the two, so a saga keyed on anything but
/// <c>Id</c> was written with a <b>server-generated <c>ObjectId</c></b> and could never be loaded
/// back: every continuation message saw "no saga" and every start accumulated another orphan
/// document.</para>
///
/// <para>Each fact drives a saga start → update → complete through a <b>real <see cref="IHost"/> with
/// real generated frames</b>, then asserts with <b>direct MongoDB reads</b> that the document's
/// <c>_id</c> is the identity value in its native BSON type, that no duplicate identity field was
/// written alongside it, and that the update and the completion delete found that same document.
/// Collection names resolve through <see cref="MongoConstants.SagaCollectionName"/>, never a
/// literal.</para>
///
/// <para>Every convention gets its own dedicated saga type: <c>BsonClassMap</c> registrations
/// and <c>MongoIdentityMapping</c>'s memo are both process-global and cannot be undone, so a
/// document type must never be shared between two scenarios.</para>
/// </summary>
[Collection("mongodb")]
public class saga_identity_conventions
{
    private readonly AppFixture _fixture;
    public saga_identity_conventions(AppFixture fixture) => _fixture = fixture;

    private async Task<IHost> BuildHostAsync()
    {
        await _fixture.ClearAll();

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Fresh compiled assembly per host (mirrors saga_atomicity / MongoDbSagaHost).
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;

                // Only this file's convention sagas — never the rest of the assembly's handlers.
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(PermitSaga))
                    .IncludeType(typeof(ParcelSaga))
                    .IncludeType(typeof(ShipmentSaga))
                    .IncludeType(typeof(MeterSaga));

                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync();
    }

    // Direct, Wolverine-independent read of the raw saga documents so the assertions see the
    // actual on-disk shape (which member landed in _id, and in what BSON type).
    private IMongoCollection<BsonDocument> RawDocuments(Type sagaType)
        => _fixture.Client.GetDatabase(AppFixture.DatabaseName)
            .GetCollection<BsonDocument>(MongoConstants.SagaCollectionName(sagaType));

    private async Task<BsonDocument> SingleDocumentAsync(Type sagaType)
    {
        var documents = await RawDocuments(sagaType).Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();

        // More than one document is the exact 1.0.0 failure mode: each "start" wrote a new
        // ObjectId-keyed orphan because the previous one could never be found.
        documents.Count.ShouldBe(1);
        return documents[0];
    }

    // ── row 1: [SagaIdentity]-attributed member (precedence tier 1), string id ─────────

    [Fact]
    public async Task saga_identity_attributed_member_is_the_document_id()
    {
        using var host = await BuildHostAsync();
        var permitNumber = "PERMIT-" + Guid.NewGuid().ToString("N");

        await host.InvokeMessageAndWaitAsync(new BeginPermit(permitNumber));

        var document = await SingleDocumentAsync(typeof(PermitSaga));
        document["_id"].BsonType.ShouldBe(BsonType.String);
        document["_id"].AsString.ShouldBe(permitNumber);
        document.Contains("PermitNumber").ShouldBeFalse();   // the id member is not duplicated
        document["Stage"].AsString.ShouldBe("issued");

        // Update: proves the generated version-guarded update frame found the same document.
        await host.InvokeMessageAndWaitAsync(new ApprovePermit(permitNumber));
        var updated = await SingleDocumentAsync(typeof(PermitSaga));
        updated["_id"].AsString.ShouldBe(permitNumber);
        updated["Stage"].AsString.ShouldBe("approved");
        updated["Version"].AsInt32.ShouldBe(2);

        // Completion: proves the generated delete frame found the same document.
        await host.InvokeMessageAndWaitAsync(new ClosePermit(permitNumber));
        (await RawDocuments(typeof(PermitSaga)).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty))
            .ShouldBe(0);
    }

    // ── row 2: {TypeName}Id (precedence tier 2), Guid id ──────────────────────────────

    [Fact]
    public async Task saga_type_name_id_convention_is_the_document_id()
    {
        using var host = await BuildHostAsync();
        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new BeginParcel(id));

        var document = await SingleDocumentAsync(typeof(ParcelSaga));
        document["_id"].BsonType.ShouldBe(BsonType.Binary);
        document["_id"].AsGuid.ShouldBe(id);
        document.Contains("ParcelSagaId").ShouldBeFalse();
        document["Stage"].AsString.ShouldBe("created");

        await host.InvokeMessageAndWaitAsync(new SortParcel(id));
        var updated = await SingleDocumentAsync(typeof(ParcelSaga));
        updated["_id"].AsGuid.ShouldBe(id);
        updated["Stage"].AsString.ShouldBe("sorted");
        updated["Version"].AsInt32.ShouldBe(2);

        await host.InvokeMessageAndWaitAsync(new DeliverParcel(id));
        (await RawDocuments(typeof(ParcelSaga)).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty))
            .ShouldBe(0);
    }

    // ── row 3: {Name-minus-Saga}Id (precedence tier 3), Guid id ───────────────────────

    [Fact]
    public async Task saga_name_minus_saga_id_convention_is_the_document_id()
    {
        using var host = await BuildHostAsync();
        var id = Guid.NewGuid();

        await host.InvokeMessageAndWaitAsync(new BeginShipment(id));

        var document = await SingleDocumentAsync(typeof(ShipmentSaga));
        document["_id"].BsonType.ShouldBe(BsonType.Binary);
        document["_id"].AsGuid.ShouldBe(id);
        document.Contains("ShipmentId").ShouldBeFalse();
        document["Stage"].AsString.ShouldBe("booked");

        await host.InvokeMessageAndWaitAsync(new ShipShipment(id));
        var updated = await SingleDocumentAsync(typeof(ShipmentSaga));
        updated["_id"].AsGuid.ShouldBe(id);
        updated["Stage"].AsString.ShouldBe("shipped");
        updated["Version"].AsInt32.ShouldBe(2);

        await host.InvokeMessageAndWaitAsync(new FinishShipment(id));
        (await RawDocuments(typeof(ShipmentSaga)).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty))
            .ShouldBe(0);
    }

    // ── row 4: SagaId (precedence tier 4), int id ─────────────────────────────────────

    [Fact]
    public async Task saga_id_member_convention_is_the_document_id()
    {
        using var host = await BuildHostAsync();
        const int id = 40417;

        await host.InvokeMessageAndWaitAsync(new BeginMeter(id));

        var document = await SingleDocumentAsync(typeof(MeterSaga));
        document["_id"].BsonType.ShouldBe(BsonType.Int32);
        document["_id"].AsInt32.ShouldBe(id);
        document.Contains("SagaId").ShouldBeFalse();
        document["Reading"].AsInt32.ShouldBe(1);

        await host.InvokeMessageAndWaitAsync(new ReadMeter(id));
        var updated = await SingleDocumentAsync(typeof(MeterSaga));
        updated["_id"].AsInt32.ShouldBe(id);
        updated["Reading"].AsInt32.ShouldBe(2);
        updated["Version"].AsInt32.ShouldBe(2);

        await host.InvokeMessageAndWaitAsync(new RetireMeter(id));
        (await RawDocuments(typeof(MeterSaga)).CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty))
            .ShouldBe(0);
    }

    // ── row 23: an inherited non-Id identity member fails at codegen, not at runtime ──

    /// <summary>
    /// A saga inheriting its identity member from a shared base (<see cref="TollBase.SagaId"/>) cannot
    /// be aligned: the driver refuses to let <see cref="TollboothSaga"/>'s own class map claim a member
    /// declared on its base. The frame constructors call the alignment helper at <b>codegen</b> time
    /// precisely so this surfaces with its two remedies named, rather than as an unfindable document on
    /// the first message.
    ///
    /// <para>The failure lands on chain <i>compilation</i>, not on <c>StartAsync</c>: Wolverine compiles
    /// handler chains lazily inside <c>HandlerGraph.HandlerFor</c>
    /// (<c>HandlerGraph.cs:279</c> — <c>InitializeSynchronously</c>), which is where
    /// <c>HandlerChain.AssembleTypes</c> constructs the frames, and there is no eager-compile hook to
    /// hoist it to host build. So this fact forces compilation the way the repo's codegen tests
    /// already do. What it proves is the load-bearing part: the exception comes out of <b>codegen</b>,
    /// before a single message is handled and before any document is written.</para>
    /// </summary>
    [Fact]
    public async Task inherited_non_id_identity_member_fails_at_codegen_with_remedies()
    {
        await _fixture.ClearAll();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(TollboothSaga));
                opts.Services.AddSingleton<IMongoClient>(_fixture.Client);
                opts.UseMongoDbPersistence(AppFixture.DatabaseName);
            }).StartAsync();

        var graph = host.Services.GetRequiredService<HandlerGraph>();
        var ex = Should.Throw<Exception>(() => graph.HandlerFor(typeof(BeginToll)));

        // Codegen wraps compilation failures, so assert on the surfaced text, not the outer type.
        var text = ex.ToString();
        text.ShouldContain($"Wolverine resolved '{nameof(TollBase.SagaId)}' as the identity member for");
        text.ShouldContain(nameof(TollboothSaga));
        text.ShouldContain(nameof(TollBase));
        text.ShouldContain("[BsonId]");

        // No document was written — the saga collection was never created.
        (await RawDocuments(typeof(TollboothSaga))
            .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty)).ShouldBe(0);
    }

    // ── row 17: the runtime leg (TypeLoadMode.Static safety net) ──────────────────────

    /// <summary>
    /// Design §4.1: in <c>TypeLoadMode.Static</c> the generated types are attached without ever
    /// calling <c>HandlerChain.AssembleTypes</c>, so <b>no frame constructor runs</b> — codegen-time
    /// alignment alone would be absent from a pre-generated deployment. Alignment therefore also
    /// happens in <c>MongoSagaOperations</c>'s collection accessor. This fact proves that leg
    /// directly: <see cref="TurbineSaga"/> is referenced by no handler and no host in this assembly,
    /// so its frames are never constructed, yet a raw insert/load/delete through
    /// <see cref="MongoSagaOperations"/> still keys the document by the resolved identity member.
    /// </summary>
    [Fact]
    public async Task operations_align_identity_without_any_frame_ever_being_constructed()
    {
        var database = _fixture.Client.GetDatabase(AppFixture.DatabaseName);
        var id = Guid.NewGuid();

        using var session = await _fixture.Client.StartSessionAsync();

        await MongoSagaOperations.InsertSagaAsync(
            database, session, new TurbineSaga { TurbineId = id, Stage = "spinning" }, CancellationToken.None);

        var document = await RawDocuments(typeof(TurbineSaga))
            .Find(Builders<BsonDocument>.Filter.Eq("_id", id)).SingleAsync();
        document["_id"].AsGuid.ShouldBe(id);
        document.Contains("TurbineId").ShouldBeFalse();

        var loaded = await MongoSagaOperations.LoadSagaAsync<TurbineSaga, Guid>(
            database, session, id, CancellationToken.None);
        loaded.ShouldNotBeNull();
        loaded.Stage.ShouldBe("spinning");

        await MongoSagaOperations.DeleteSagaAsync<TurbineSaga, Guid>(
            database, session, id, CancellationToken.None);
        (await RawDocuments(typeof(TurbineSaga))
            .CountDocumentsAsync(Builders<BsonDocument>.Filter.Eq("_id", id))).ShouldBe(0);
    }
}

// ── one dedicated saga type per identity convention ─────────────────────────────────
//
// Class maps and the alignment memo are process-global (design §9.1), so these types are
// deliberately used by exactly one scenario each and by nothing else in the assembly.

/// <summary>Precedence tier 1 — <c>[SagaIdentity]</c> on a member named nothing like "Id".</summary>
public class PermitSaga : Saga
{
    [SagaIdentity] public string PermitNumber { get; set; } = string.Empty;
    public string Stage { get; set; } = "none";

    public void Start(BeginPermit message)
    {
        PermitNumber = message.PermitNumber;
        Stage = "issued";
    }

    public void Handle(ApprovePermit message) => Stage = "approved";
    public void Handle(ClosePermit message) => MarkCompleted();
}

public record BeginPermit([property: SagaIdentity] string PermitNumber);
public record ApprovePermit([property: SagaIdentity] string PermitNumber);
public record ClosePermit([property: SagaIdentity] string PermitNumber);

/// <summary>Precedence tier 2 — <c>{TypeName}Id</c>.</summary>
public class ParcelSaga : Saga
{
    public Guid ParcelSagaId { get; set; }
    public string Stage { get; set; } = "none";

    public void Start(BeginParcel message)
    {
        ParcelSagaId = message.ParcelSagaId;
        Stage = "created";
    }

    public void Handle(SortParcel message) => Stage = "sorted";
    public void Handle(DeliverParcel message) => MarkCompleted();
}

public record BeginParcel(Guid ParcelSagaId);
public record SortParcel(Guid ParcelSagaId);
public record DeliverParcel(Guid ParcelSagaId);

/// <summary>Precedence tier 3 — <c>{Name-minus-Saga}Id</c>, the review's example shape.</summary>
public class ShipmentSaga : Saga
{
    public Guid ShipmentId { get; set; }
    public string Stage { get; set; } = "none";

    public void Start(BeginShipment message)
    {
        ShipmentId = message.ShipmentId;
        Stage = "booked";
    }

    public void Handle(ShipShipment message) => Stage = "shipped";
    public void Handle(FinishShipment message) => MarkCompleted();
}

public record BeginShipment(Guid ShipmentId);
public record ShipShipment(Guid ShipmentId);
public record FinishShipment(Guid ShipmentId);

/// <summary>Precedence tier 4 — <c>SagaId</c>.</summary>
public class MeterSaga : Saga
{
    public int SagaId { get; set; }
    public int Reading { get; set; }

    public void Start(BeginMeter message)
    {
        SagaId = message.SagaId;
        Reading = 1;
    }

    public void Handle(ReadMeter message) => Reading++;
    public void Handle(RetireMeter message) => MarkCompleted();
}

public record BeginMeter(int SagaId);
public record ReadMeter(int SagaId);
public record RetireMeter(int SagaId);

/// <summary>
/// A shared saga base contributing a non-<c>Id</c> identity member — the shape the driver cannot map
/// from the subclass's own class map. Used only by the codegen-failure fact.
/// </summary>
public abstract class TollBase : Saga
{
    public Guid SagaId { get; set; }
}

public class TollboothSaga : TollBase
{
    public string Stage { get; set; } = "none";

    public void Start(BeginToll message) => Stage = "open";
    public void Handle(CloseToll message) => MarkCompleted();
}

public record BeginToll(Guid SagaId);
public record CloseToll(Guid SagaId);

/// <summary>
/// Tier 3, used <b>only</b> by the runtime-leg fact. Deliberately handler-free so no frame is
/// ever constructed for it anywhere in this assembly.
/// </summary>
public class TurbineSaga : Saga
{
    public Guid TurbineId { get; set; }
    public string Stage { get; set; } = "none";
}
