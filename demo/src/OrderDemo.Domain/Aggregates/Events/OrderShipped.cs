namespace OrderDemo.Domain.Aggregates.Events;

/// <summary>Domain event raised when an order is shipped.</summary>
public sealed record OrderShipped(Guid OrderId, DateTimeOffset ShippedAt);
