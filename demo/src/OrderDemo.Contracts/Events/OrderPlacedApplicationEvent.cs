namespace OrderDemo.Contracts.Events;

/// <summary>
/// Application event published to RabbitMQ (via Wolverine outbox) when an order is placed.
/// Consumed by the read-side projector to build the order summary read model.
/// </summary>
public sealed record OrderPlacedApplicationEvent(
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    DateTimeOffset PlacedAt);
