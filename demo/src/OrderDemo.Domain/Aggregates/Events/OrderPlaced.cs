namespace OrderDemo.Domain.Aggregates.Events;

/// <summary>Domain event raised when a new order is placed.</summary>
public sealed record OrderPlaced(
    Guid OrderId,
    Guid CustomerId,
    IReadOnlyList<OrderItem> Items,
    decimal TotalAmount,
    DateTimeOffset PlacedAt);
