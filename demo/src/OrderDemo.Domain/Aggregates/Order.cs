using OrderDemo.Domain.Aggregates.Events;

namespace OrderDemo.Domain.Aggregates;

public enum OrderStatus { Pending, Shipped, Cancelled }

/// <summary>
/// The Order aggregate root. All state mutations go through factory/behaviour
/// methods that register domain events. The aggregate never publishes events
/// externally — callers drain <see cref="DrainDomainEvents"/> after saving.
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
    public decimal DiscountPercent { get; private set; }
    public OrderStatus Status { get; private set; }
    public DateTimeOffset PlacedAt { get; private set; }
    public DateTimeOffset? ShippedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public string? CancelReason { get; private set; }

    /// <summary>
    /// Optimistic concurrency token. Incremented on each mutation.
    /// <see cref="Infrastructure.Persistence.OrderRepository.UpdateAsync"/> filters on this value
    /// and throws <see cref="InvalidOperationException"/> if no document matches (concurrent update).
    /// </summary>
    public int Version { get; private set; }

    /// <summary>
    /// Create a new order and register the <see cref="OrderPlaced"/> domain event.
    /// Rules: 1–10 items, each with a positive quantity and unit price.
    /// </summary>
    public static Order Place(Guid customerId, IReadOnlyList<OrderItem> items)
    {
        if (items.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(items), "An order must contain at least one item.");
        if (items.Count > 10)
            throw new ArgumentOutOfRangeException(nameof(items), "An order cannot contain more than 10 items.");
        if (items.Any(i => i.Quantity <= 0))
            throw new ArgumentException("All items must have a positive quantity.", nameof(items));
        if (items.Any(i => i.UnitPrice <= 0))
            throw new ArgumentException("All items must have a positive unit price.", nameof(items));

        var order = new Order
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Items = items,
            TotalAmount = items.Sum(i => i.LineTotal),
            Status = OrderStatus.Pending,
            PlacedAt = DateTimeOffset.UtcNow,
            Version = 0
        };

        order._domainEvents.Add(new OrderPlaced(order.Id, customerId, items, order.TotalAmount, order.PlacedAt));
        return order;
    }

    /// <summary>Mark the order as shipped and register the <see cref="OrderShipped"/> domain event.</summary>
    public void Ship()
    {
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException($"Cannot ship an order with status '{Status}'.");

        ShippedAt = DateTimeOffset.UtcNow;
        Status = OrderStatus.Shipped;
        Version++;
        _domainEvents.Add(new OrderShipped(Id, ShippedAt.Value));
    }

    /// <summary>
    /// Cancel the order with an optional reason. Only pending orders can be cancelled.
    /// </summary>
    public void Cancel(string? reason = null)
    {
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException($"Cannot cancel an order with status '{Status}'.");

        CancelledAt = DateTimeOffset.UtcNow;
        CancelReason = reason;
        Status = OrderStatus.Cancelled;
        Version++;
        _domainEvents.Add(new OrderCancelled(Id, reason ?? string.Empty, CancelledAt.Value));
    }

    /// <summary>
    /// Apply a percentage discount (0 &lt; percent ≤ 50) to the order total.
    /// Only pending orders can receive a discount.
    /// Multiple discounts stack — each call applies to the current <see cref="TotalAmount"/>.
    /// </summary>
    public void ApplyDiscount(decimal discountPercent)
    {
        if (discountPercent <= 0 || discountPercent > 50)
            throw new ArgumentOutOfRangeException(nameof(discountPercent),
                "Discount must be greater than 0 and at most 50 percent.");
        if (Status != OrderStatus.Pending)
            throw new InvalidOperationException($"Cannot apply a discount to an order with status '{Status}'.");

        DiscountPercent += discountPercent;
        TotalAmount = TotalAmount * (1 - discountPercent / 100m);
        Version++;
        _domainEvents.Add(new DiscountApplied(Id, discountPercent, TotalAmount, DateTimeOffset.UtcNow, DiscountPercent));
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
