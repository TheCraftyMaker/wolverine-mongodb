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
/// - All writes are idempotent and use absolute values, so retries are safe.
///
/// <b>Idempotency design:</b>
/// - <c>OrderPlaced</c> uses <c>InsertIfNotExistsAsync</c> ($setOnInsert) so a late/retried
///   event never overwrites a summary already advanced to Shipped/Cancelled.
/// - All other handlers assign fields absolutely (no +=) using values from the event.
/// </summary>
[Wolverine.Attributes.WolverineHandler]
public static class OrderSummaryProjector
{
    public static async Task Handle(
        OrderPlacedApplicationEvent evt,
        OrderSummaryRepository summaries,
        CancellationToken ct)
    {
        // InsertIfNotExistsAsync uses $setOnInsert so a late/retried OrderPlaced
        // after Ship or Cancel does NOT reset the summary back to Pending.
        await summaries.InsertIfNotExistsAsync(
            new OrderSummary
            {
                OrderId = evt.OrderId,
                CustomerId = evt.CustomerId,
                TotalAmount = evt.TotalAmount,
                Status = "Pending",
                PlacedAt = evt.PlacedAt
            },
            ct);
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
        summary.DiscountPercent = evt.CumulativeDiscountPercent;

        await summaries.UpsertAsync(summary, ct);
    }
}
