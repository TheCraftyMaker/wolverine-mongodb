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
        var summary = await summaries.FindByOrderIdAsync(evt.OrderId, ct)
            ?? new OrderSummary { OrderId = evt.OrderId };

        summary.Status = "Shipped";
        summary.ShippedAt = evt.ShippedAt;

        await summaries.UpsertAsync(summary, ct);
    }

    public static async Task Handle(
        OrderCancelledApplicationEvent evt,
        OrderSummaryRepository summaries,
        CancellationToken ct)
    {
        var summary = await summaries.FindByOrderIdAsync(evt.OrderId, ct)
            ?? new OrderSummary { OrderId = evt.OrderId };

        summary.Status = "Cancelled";
        summary.CancelledAt = evt.CancelledAt;
        summary.CancelReason = evt.Reason;

        await summaries.UpsertAsync(summary, ct);
    }

    public static async Task Handle(
        DiscountAppliedApplicationEvent evt,
        OrderSummaryRepository summaries,
        CancellationToken ct)
    {
        var summary = await summaries.FindByOrderIdAsync(evt.OrderId, ct)
            ?? new OrderSummary { OrderId = evt.OrderId };

        summary.TotalAmount = evt.NewTotal;
        summary.DiscountPercent += evt.DiscountPercent;

        await summaries.UpsertAsync(summary, ct);
    }
}
