using OrderDemo.Domain.Aggregates.Events;

namespace OrderDemo.Domain.Aggregates;

public enum OrderStatus { Pending, Shipped, Cancelled }

/// <summary>
/// The Order aggregate root. All state mutations go through factory/behaviour
/// methods that register domain events. The aggregate never publishes events
/// externally — callers drain <see cref="GetDomainEvents"/> after saving.
/// </summary>
public sealed class Order
{
    private readonly List<object> _domainEvents = [];

    // MongoDB driver requires a parameterless constructor (or BSON class map)
    private Order() { }

    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public IReadOnlyList<OrderItem> Items { get; private set; } = [];
    public decimal TotalAmount { get; private set; }
    public OrderStatus Status { get; private set; }
    public DateTimeOffset PlacedAt { get; private set; }
    public DateTimeOffset? ShippedAt { get; private set; }

    /// <summary>Create a new order and register the <see cref="OrderPlaced"/> domain event.</summary>
    public static Order Place(Guid customerId, IReadOnlyList<OrderItem> items)
    {
        ArgumentOutOfRangeException.ThrowIfZero(items.Count);

        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Items = items,
            TotalAmount = items.Sum(i => i.LineTotal),
            Status = OrderStatus.Pending,
            PlacedAt = DateTimeOffset.UtcNow
        };

        order._domainEvents.Add(new OrderPlaced(order.Id, customerId, items, order.TotalAmount, order.PlacedAt));
        return order;
    }

    /// <summary>Mark the order as shipped and register the <see cref="OrderShipped"/> domain event.</summary>
    public void Ship()
    {
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException($"Cannot ship an order with status {Status}.");

        ShippedAt = DateTimeOffset.UtcNow;
        Status = OrderStatus.Shipped;
        _domainEvents.Add(new OrderShipped(Id, ShippedAt.Value));
    }

    /// <summary>
    /// Drain all pending domain events. Call this after saving the aggregate to
    /// map domain events to application events and cascade them via Wolverine.
    /// </summary>
    public IReadOnlyList<object> DrainDomainEvents()
    {
        var events = _domainEvents.ToList();
        _domainEvents.Clear();
        return events;
    }
}
