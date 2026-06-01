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
///   1. Create Order aggregate (registers <c>OrderPlaced</c> domain event internally).
///   2. Persist to the orders collection via the Wolverine-managed session.
///   3. Drain the aggregate's domain events and map each one to the corresponding
///      application event. The returned application event is cascaded by Wolverine
///      and routed through the transactional outbox to RabbitMQ.
///
/// Because the Wolverine.MongoDB persistence frame wraps this entire handler in a
/// MongoDB transaction, the order write and the outbox entry for the application
/// event are committed atomically — the outbox guarantee is preserved even on crash.
/// </summary>
public static class PlaceOrderHandler
{
    public static async Task<OrderPlacedApplicationEvent> Handle(
        PlaceOrderCommand cmd,
        IClientSessionHandle session,   // provided by the Wolverine MongoDB transactional frame
        IOrderRepository orders,
        CancellationToken ct)
    {
        var order = Order.Place(cmd.CustomerId, cmd.Items);
        await orders.AddAsync(order, session, ct);

        // Drain domain events from the aggregate and map to application events.
        // The returned value is cascaded by Wolverine through the outbox → RabbitMQ.
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
