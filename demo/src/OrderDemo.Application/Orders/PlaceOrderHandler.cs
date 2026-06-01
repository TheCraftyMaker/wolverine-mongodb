using MongoDB.Driver;
using OrderDemo.Contracts.Commands;
using OrderDemo.Contracts.Events;
using OrderDemo.Domain.Aggregates;
using OrderDemo.Infrastructure.Persistence;

namespace OrderDemo.Application.Orders;

/// <summary>
/// Handles <see cref="PlaceOrderCommand"/>.
///
/// Flow (within a single MongoDB outbox transaction managed by Wolverine):
///   1. Map command DTOs to domain value objects.
///   2. Create Order aggregate (registers <c>OrderPlaced</c> domain event internally).
///   3. Persist to the orders collection via the Wolverine-managed session.
///   4. Drain the aggregate's domain events and map to the application event
///      cascaded by Wolverine through the transactional outbox.
///
/// Because the Wolverine.MongoDB persistence frame wraps this handler in a
/// MongoDB transaction, the order write and the outbox entry are committed
/// atomically — the outbox guarantee is preserved even on crash.
/// </summary>
public static class PlaceOrderHandler
{
    public static async Task<OrderPlacedApplicationEvent> Handle(
        PlaceOrderCommand cmd,
        IClientSessionHandle session,
        IOrderRepository orders,
        CancellationToken ct)
    {
        var items = cmd.Items
            .Select(i => new OrderItem(i.ProductId, i.ProductName, i.Quantity, i.UnitPrice))
            .ToList();

        var order = Order.Place(cmd.CustomerId, items);
        await orders.AddAsync(order, session, ct);

        var domainEvent = order.DrainDomainEvents()
            .OfType<Domain.Aggregates.Events.OrderPlaced>()
            .Single();

        return new OrderPlacedApplicationEvent(
            domainEvent.OrderId,
            domainEvent.CustomerId,
            domainEvent.TotalAmount,
            domainEvent.PlacedAt);
    }
}
