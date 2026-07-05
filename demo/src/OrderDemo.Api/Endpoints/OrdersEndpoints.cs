using OrderDemo.Contracts.Commands;
using OrderDemo.Contracts.Commands.Audit;
using OrderDemo.Contracts.Commands.Notes;
using OrderDemo.Infrastructure.Persistence;
using Wolverine;

namespace OrderDemo.Api.Endpoints;

/// <summary>
/// Minimal API endpoint registration for the Orders module.
/// Command endpoints dispatch via <see cref="IMessageBus"/>; the GET endpoint
/// reads from the projected read model directly.
/// </summary>
public static class OrdersEndpoints
{
    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var orders = app.MapGroup("/orders").WithTags("Orders");

        // POST /orders — place a new order
        orders.MapPost("/", async (PlaceOrderCommand cmd, IMessageBus bus) =>
        {
            var evt = await bus.InvokeAsync<Contracts.Events.OrderPlacedApplicationEvent>(cmd);
            return Results.Created($"/orders/{evt.OrderId}", new { evt.OrderId });
        })
        .WithSummary("Place a new order")
        .WithDescription(
            "Creates a new order and publishes OrderPlacedApplicationEvent via the transactional outbox → RabbitMQ. " +
            "The read model is updated asynchronously once the projector consumes the event.");

        // POST /orders/{id}/ship — ship an existing order
        orders.MapPost("/{id:guid}/ship", async (Guid id, IMessageBus bus) =>
        {
            await bus.InvokeAsync(new ShipOrderCommand(id));
            return Results.Accepted();
        })
        .WithSummary("Ship a pending order");

        // POST /orders/{id}/cancel — cancel a pending order
        orders.MapPost("/{id:guid}/cancel", async (Guid id, CancelBody? body, IMessageBus bus) =>
        {
            await bus.InvokeAsync(new CancelOrderCommand(id, body?.Reason));
            return Results.Accepted();
        })
        .WithSummary("Cancel a pending order");

        // POST /orders/{id}/discount — apply a discount to a pending order
        orders.MapPost("/{id:guid}/discount", async (Guid id, DiscountBody body, IMessageBus bus) =>
        {
            await bus.InvokeAsync(new ApplyDiscountCommand(id, body.DiscountPercent));
            return Results.Accepted();
        })
        .WithSummary("Apply a percentage discount (0–50%) to a pending order");

        // POST /orders/{id}/confirm-delivery — confirm delivery, completing the fulfillment saga
        orders.MapPost("/{id:guid}/confirm-delivery", async (Guid id, ConfirmBody body, IMessageBus bus) =>
        {
            await bus.InvokeAsync(new ConfirmDeliveryCommand(id, body.DeliveredAt));
            return Results.Accepted();
        })
        .WithSummary("Confirm delivery of a shipped order (completes the OrderFulfillmentSaga)");

        // POST /orders/{id}/notes — add a note to an order (Tier-1 [Entity]/IStorageAction<T> showcase)
        orders.MapPost("/{id:guid}/notes", async (Guid id, NoteBody body, IMessageBus bus) =>
        {
            await bus.InvokeAsync(new AddOrderNoteCommand(id, body.Text, body.Author));
            return Results.Accepted();
        })
        .WithSummary("Add a note to an order")
        .WithDescription(
            "Demonstrates Insert<OrderNote> as a return-value side effect — no repository, no manual " +
            "session. The generated frame persists the note inside the same MongoDB transaction as the outbox.");

        // POST /orders/notes/{noteId} — edit an existing note
        orders.MapPost("/notes/{noteId}", async (string noteId, EditNoteBody body, IMessageBus bus) =>
        {
            await bus.InvokeAsync(new EditOrderNoteCommand(noteId, body.NewText));
            return Results.Accepted();
        })
        .WithSummary("Edit an order note")
        .WithDescription("Demonstrates [Entity(\"NoteId\")] loading + Update<OrderNote> as a return-value side effect.");

        // DELETE /orders/notes/{noteId} — delete an existing note
        orders.MapDelete("/notes/{noteId}", async (string noteId, IMessageBus bus) =>
        {
            await bus.InvokeAsync(new DeleteOrderNoteCommand(noteId));
            return Results.Accepted();
        })
        .WithSummary("Delete an order note")
        .WithDescription("Demonstrates [Entity(\"NoteId\")] loading + Delete<OrderNote> as a return-value side effect.");

        // POST /orders/{id}/audit — record an audit entry (MongoDbUnitOfWork showcase)
        orders.MapPost("/{id:guid}/audit", async (Guid id, AuditBody body, IMessageBus bus) =>
        {
            await bus.InvokeAsync(new RecordOrderAuditCommand(id, body.Action, body.PerformedBy));
            return Results.Accepted();
        })
        .WithSummary("Record an audit entry for an order")
        .WithDescription(
            "Demonstrates MongoDbUnitOfWork as a write surface: the handler writes directly to the " +
            "order_audit_entries collection through uow.Collection<T>(name), with the session threaded " +
            "automatically so the write commits atomically with the outbox.");

        // GET /orders — list all orders (read model)
        orders.MapGet("/", async (OrderSummaryRepository summaries, CancellationToken ct) =>
        {
            var results = await summaries.GetAllAsync(ct);
            return Results.Ok(results);
        })
        .WithSummary("List all orders (read model)")
        .WithDescription("Returns the denormalized OrderSummary read model updated by the projector.");

        return app;
    }

    public sealed record CancelBody(string? Reason);
    public sealed record DiscountBody(decimal DiscountPercent);
    public sealed record ConfirmBody(DateTimeOffset DeliveredAt);
    public sealed record NoteBody(string Text, string Author);
    public sealed record EditNoteBody(string NewText);
    public sealed record AuditBody(string Action, string PerformedBy);
}
