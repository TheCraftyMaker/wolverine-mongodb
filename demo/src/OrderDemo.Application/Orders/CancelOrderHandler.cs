using MongoDB.Driver;
using OrderDemo.Contracts.Commands;
using OrderDemo.Contracts.Events;
using OrderDemo.Domain.Aggregates.Events;
using OrderDemo.Infrastructure.Persistence;

namespace OrderDemo.Application.Orders;

/// <summary>
/// Handles <see cref="CancelOrderCommand"/>.
///
/// The order status transition and the outbox entry for <see cref="OrderCancelledApplicationEvent"/>
/// are committed atomically in the Wolverine-managed MongoDB transaction.
/// Only pending orders can be cancelled — the aggregate enforces this rule.
/// </summary>
public static class CancelOrderHandler
{
    public static async Task<OrderCancelledApplicationEvent> Handle(
        CancelOrderCommand cmd,
        IClientSessionHandle session,
        IOrderRepository orders,
        CancellationToken ct)
    {
        var order = await orders.FindAsync(cmd.OrderId, ct)
            ?? throw new InvalidOperationException($"Order {cmd.OrderId} not found.");

        order.Cancel(cmd.Reason);
        await orders.UpdateAsync(order, session, ct);

        var evt = order.DrainDomainEvents().OfType<OrderCancelled>().Single();
        return new OrderCancelledApplicationEvent(evt.OrderId, evt.Reason, evt.CancelledAt);
    }
}
