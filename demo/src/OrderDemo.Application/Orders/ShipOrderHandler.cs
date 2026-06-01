using MongoDB.Driver;
using OrderDemo.Contracts.Commands;
using OrderDemo.Contracts.Events;
using OrderDemo.Infrastructure.Persistence;

namespace OrderDemo.Application.Orders;

/// <summary>
/// Handles <see cref="ShipOrderCommand"/>.
///
/// Same transactional guarantee as <see cref="PlaceOrderHandler"/>: the order update
/// and the outbox entry for <see cref="OrderShippedApplicationEvent"/> are committed
/// atomically via the Wolverine-managed MongoDB session.
/// </summary>
public static class ShipOrderHandler
{
    public static async Task<OrderShippedApplicationEvent> Handle(
        ShipOrderCommand cmd,
        IClientSessionHandle session,
        IOrderRepository orders,
        CancellationToken ct)
    {
        var order = await orders.FindAsync(cmd.OrderId, ct)
            ?? throw new InvalidOperationException($"Order {cmd.OrderId} not found.");

        order.Ship();
        await orders.UpdateAsync(order, session, ct);

        var domainEvent = order.DrainDomainEvents()
            .OfType<Domain.Aggregates.Events.OrderShipped>()
            .Single();

        return new OrderShippedApplicationEvent(domainEvent.OrderId, domainEvent.ShippedAt);
    }
}
