using OrderDemo.Contracts.Commands.Notes;
using OrderDemo.Infrastructure.Persistence;
using Wolverine.Persistence;

namespace OrderDemo.Application.Notes;

/// <summary>
/// Demonstrates the Tier-1 generic entity persistence surface:
///   Insert&lt;T&gt;  — create a new entity (no prior document required)
///   Update&lt;T&gt;  — mutate a loaded entity (entity loaded via [Entity])
///   Delete&lt;T&gt;  — remove a loaded entity (entity loaded via [Entity])
///
/// Wolverine's generated frame opens the TransactionalFrame session, runs the handler,
/// then persists the returned storage action inside the same transaction — the entity
/// write and any cascaded outbox entries commit atomically. No manual session needed.
/// </summary>
public static class OrderNoteHandler
{
    // ── Insert ────────────────────────────────────────────────────────────────
    // Returning Insert<OrderNote> causes Wolverine to call DetermineInsertFrame:
    // the entity is upserted into the "ordernote" collection inside the transaction.
    public static Insert<OrderNote> Handle(AddOrderNoteCommand cmd)
        => new(new OrderNote
        {
            Id = Guid.NewGuid().ToString(),
            OrderId = cmd.OrderId,
            Text = cmd.Text,
            Author = cmd.Author,
            CreatedAt = DateTimeOffset.UtcNow
        });

    // ── Update ────────────────────────────────────────────────────────────────
    // [Entity("NoteId")] tells Wolverine to load the OrderNote whose _id == cmd.NoteId.
    // If not found (Required=true by default) the handler is skipped (404 behaviour).
    // The loaded note is mutated in-place; returning Update<OrderNote> persists it.
    public static Update<OrderNote> Handle(EditOrderNoteCommand cmd, [Entity("NoteId")] OrderNote note)
    {
        note.Text = cmd.NewText;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        return new Update<OrderNote>(note);
    }

    // ── Delete ────────────────────────────────────────────────────────────────
    // [Entity("NoteId")] loads the note; returning Delete<OrderNote> removes it.
    public static Delete<OrderNote> Handle(DeleteOrderNoteCommand cmd, [Entity("NoteId")] OrderNote note)
        => new(note);
}
