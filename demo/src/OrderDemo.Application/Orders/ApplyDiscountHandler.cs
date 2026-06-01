using MongoDB.Driver;
using OrderDemo.Contracts.Commands;
using OrderDemo.Contracts.Events;
using OrderDemo.Domain.Aggregates.Events;
using OrderDemo.Infrastructure.Persistence;

namespace OrderDemo.Application.Orders;

/// <summary>
/// Handles <see cref="ApplyDiscountCommand"/>.
///
/// Applies a percentage discount (0 &lt; percent ≤ 50) to a pending order and persists
/// both the updated aggregate and the outbox entry for <see cref="DiscountAppliedApplicationEvent"/>
/// in the same MongoDB transaction.
/// </summary>
public static class ApplyDiscountHandler
{
    public static async Task<DiscountAppliedApplicationEvent> Handle(
        ApplyDiscountCommand cmd,
        IClientSessionHandle session,
        IOrderRepository orders,
        CancellationToken ct)
    {
        var order = await orders.FindAsync(cmd.OrderId, ct)
            ?? throw new InvalidOperationException($"Order {cmd.OrderId} not found.");

        order.ApplyDiscount(cmd.DiscountPercent);
        await orders.UpdateAsync(order, session, ct);

        var evt = order.DrainDomainEvents().OfType<DiscountApplied>().Single();
        return new DiscountAppliedApplicationEvent(evt.OrderId, evt.DiscountPercent, evt.NewTotal, evt.CumulativeDiscountPercent);
    }
}
