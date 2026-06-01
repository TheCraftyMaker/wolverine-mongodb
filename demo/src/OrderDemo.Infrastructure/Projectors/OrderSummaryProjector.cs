using MongoDB.Driver;
using OrderDemo.Contracts.Events;
using OrderDemo.Infrastructure.Persistence;

namespace OrderDemo.Infrastructure.Projectors;

/// <summary>
/// Read-side projector that consumes application events from RabbitMQ and updates
/// the <see cref="OrderSummary"/> read model in MongoDB.
///
/// This handler runs on the <c>order-projections</c> queue backed by a durable inbox
/// (configured in Program.cs), so:
/// - Each message is persisted to the inbox before processing.
/// - If the process crashes mid-projection, the message is recovered and retried.
/// - The upsert write is idempotent by <c>OrderId</c>, so retries are safe.
///
/// Note: this handler does NOT use a MongoDB session / transaction. The inbox
/// persistence and the idempotent upsert together provide the at-least-once-and-
/// idempotent guarantee without needing full ACID transactions on the read side.
/// </summary>
[Wolverine.Attributes.WolverineHandler]
public static class OrderSummaryProjector
{
    public static async Task Handle(
        OrderPlacedApplicationEvent evt,
        OrderSummaryRepository summaries,
        CancellationToken ct)
    {
        var summary = new OrderSummary
        {
            OrderId = evt.OrderId,
            CustomerId = evt.CustomerId,
            TotalAmount = evt.TotalAmount,
            Status = "Pending",
            PlacedAt = evt.PlacedAt
        };

        await summaries.UpsertAsync(summary, ct);
    }

    public static async Task Handle(
        OrderShippedApplicationEvent evt,
        OrderSummaryRepository summaries,
        CancellationToken ct)
    {
        // Load the existing summary and update the shipped fields.
        // The upsert creates a minimal document if for some reason the placed event
        // hasn't been projected yet (e.g. out-of-order delivery edge case).
        var existing = (await summaries.GetAllAsync(ct))
            .FirstOrDefault(s => s.OrderId == evt.OrderId);

        var summary = existing ?? new OrderSummary { OrderId = evt.OrderId };
        summary.Status = "Shipped";
        summary.ShippedAt = evt.ShippedAt;

        await summaries.UpsertAsync(summary, ct);
    }
}
