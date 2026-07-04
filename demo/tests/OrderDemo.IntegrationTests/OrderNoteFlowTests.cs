using FluentAssertions;
using MongoDB.Driver;
using OrderDemo.Contracts.Commands.Notes;
using OrderDemo.Infrastructure.Persistence;
using Wolverine;
using Wolverine.Tracking;

namespace OrderDemo.IntegrationTests;

/// <summary>
/// Safety-net integration tests for the demo Tier-1 generic-persistence showcase (T1.3):
/// <c>Insert&lt;OrderNote&gt;</c>/<c>Update&lt;OrderNote&gt;</c>/<c>Delete&lt;OrderNote&gt;</c>
/// return values and <c>[Entity("NoteId")]</c> parameter loading, alongside the existing
/// repository + <see cref="MongoDB.Driver.IClientSessionHandle"/> pattern used elsewhere in the demo.
///
/// The <c>OrderNote</c> document is read straight from MongoDB (bypassing Wolverine) to
/// independently verify persistence — the collection name is the library's un-prefixed entity
/// convention: <c>type.Name.ToLowerInvariant()</c> = <c>ordernote</c>.
/// </summary>
[Collection("orders")]
public class OrderNoteFlowTests(OrdersFixture fixture)
{
    // MongoConstants.EntityCollectionName(typeof(OrderNote)) — internal to the library, so the
    // exact value is reproduced here, mirroring SagaFlowTests' SagaCollection constant.
    private const string NoteCollection = "ordernote";

    private static Task<OrderNote?> LoadNoteAsync(IMongoDatabase mongo, string id)
        => mongo.GetCollection<OrderNote>(NoteCollection)
            .Find(Builders<OrderNote>.Filter.Eq(n => n.Id, id))
            .FirstOrDefaultAsync()!;

    // ── can_add_order_note ──────────────────────────────────────────────────────

    [Fact]
    public async Task Can_Add_Order_Note()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var orderId = Guid.NewGuid();
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new AddOrderNoteCommand(orderId, "Please gift-wrap", "alice"));

        var note = await mongo.GetCollection<OrderNote>(NoteCollection)
            .Find(Builders<OrderNote>.Filter.Eq(n => n.OrderId, orderId))
            .FirstOrDefaultAsync();

        note.Should().NotBeNull("Insert<OrderNote> must persist the note via the generated entity frame");
        note!.OrderId.Should().Be(orderId);
        note.Text.Should().Be("Please gift-wrap");
        note.Author.Should().Be("alice");
        note.UpdatedAt.Should().BeNull();
    }

    // ── can_edit_order_note ─────────────────────────────────────────────────────

    [Fact]
    public async Task Can_Edit_Order_Note()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var orderId = Guid.NewGuid();
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new AddOrderNoteCommand(orderId, "original text", "alice"));

        var created = await mongo.GetCollection<OrderNote>(NoteCollection)
            .Find(Builders<OrderNote>.Filter.Eq(n => n.OrderId, orderId))
            .FirstOrDefaultAsync();
        created.Should().NotBeNull();

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new EditOrderNoteCommand(created!.Id, "edited text"));

        var edited = await LoadNoteAsync(mongo, created.Id);
        edited.Should().NotBeNull();
        edited!.Text.Should().Be("edited text", "Update<OrderNote> must overwrite the loaded [Entity]");
        edited.UpdatedAt.Should().NotBeNull();
    }

    // ── can_delete_order_note ───────────────────────────────────────────────────

    [Fact]
    public async Task Can_Delete_Order_Note()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var orderId = Guid.NewGuid();
        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new AddOrderNoteCommand(orderId, "to be deleted", "alice"));

        var created = await mongo.GetCollection<OrderNote>(NoteCollection)
            .Find(Builders<OrderNote>.Filter.Eq(n => n.OrderId, orderId))
            .FirstOrDefaultAsync();
        created.Should().NotBeNull();

        await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new DeleteOrderNoteCommand(created!.Id));

        (await LoadNoteAsync(mongo, created.Id))
            .Should().BeNull("Delete<OrderNote> must remove the document via the generated delete frame");
    }

    // ── edit_note_not_found_is_skipped ──────────────────────────────────────────

    [Fact]
    public async Task Edit_Note_NotFound_Is_Skipped()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);
        var mongo = host.Services.GetRequiredService<IMongoDatabase>();

        var missingId = Guid.NewGuid().ToString();

        var act = async () => await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new EditOrderNoteCommand(missingId, "should never be written"));

        // Required=true (the [Entity] default): a missing entity short-circuits the handler
        // end-to-end rather than throwing — matching the library's entity_atomicity coverage.
        await act.Should().NotThrowAsync();

        (await LoadNoteAsync(mongo, missingId)).Should().BeNull("the handler body must never have run");
    }

    // ── delete_note_not_found_is_skipped ────────────────────────────────────────

    [Fact]
    public async Task Delete_Note_NotFound_Is_Skipped()
    {
        var db = OrdersFixture.CreateDatabaseName();
        using var host = await fixture.CreateHostAsync(db);

        var missingId = Guid.NewGuid().ToString();

        var act = async () => await host.TrackActivity().Timeout(TimeSpan.FromSeconds(30))
            .InvokeMessageAndWaitAsync(new DeleteOrderNoteCommand(missingId));

        await act.Should().NotThrowAsync("a missing required [Entity] must skip the handler, not throw");
    }
}
