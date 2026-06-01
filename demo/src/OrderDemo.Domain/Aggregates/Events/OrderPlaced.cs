namespace OrderDemo.Domain.Aggregates.Events;

/// <summary>Domain event raised when a new order is placed.</summary>
public sealed record OrderPlaced(
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    DateTimeOffset PlacedAt);
