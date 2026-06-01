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
///   2. Create Order aggregate.
///   3. Persist the order via the Wolverine-managed session.
///   4. Process domain events: for <c>OrderPlaced</c>, reserve inventory stock
///      within the SAME transaction. If stock is insufficient, the exception
///      aborts the transaction and rolls back the order write.
///   5. Return the application event — Wolverine cascades it to the outbox
///      (also within the same transaction), guaranteeing atomicity.
/// </summary>
public static class PlaceOrderHandler
{
    public static async Task<OrderPlacedApplicationEvent> Handle(
        PlaceOrderCommand cmd,
        IClientSessionHandle session,
        IOrderRepository orders,
        IInventoryRepository inventory,
        CancellationToken ct)
    {
        var items = cmd.Items
            .Select(i => new OrderItem(i.ProductId, i.ProductName, i.Quantity, i.UnitPrice))
            .ToList();

        var order = Order.Place(cmd.CustomerId, items);
        await orders.AddAsync(order, session, ct);

        // Domain event side effects — execute within the same transaction.
        // If inventory reservation fails, the entire transaction (including the
        // order write and any outbox entry) is rolled back.
        foreach (var evt in order.DrainDomainEvents())
        {
            if (evt is Domain.Aggregates.Events.OrderPlaced placed)
            {
                await ReserveInventoryAsync(placed, items, inventory, session, ct);
            }
        }

        return new OrderPlacedApplicationEvent(
            order.Id,
            order.CustomerId,
            order.TotalAmount,
            order.PlacedAt);
    }

    private static async Task ReserveInventoryAsync(
        Domain.Aggregates.Events.OrderPlaced _,
        IReadOnlyList<OrderItem> items,
        IInventoryRepository inventory,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        foreach (var item in items)
        {
            var product = await inventory.FindByIdAsync(item.ProductId, ct)
                ?? throw new InvalidOperationException(
                    $"Product '{item.ProductName}' ({item.ProductId}) not found in inventory.");

            product.Reserve(item.Quantity);
            await inventory.UpdateAsync(product, session, ct);
        }
    }
}
