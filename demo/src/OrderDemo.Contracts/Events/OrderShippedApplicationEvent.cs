namespace OrderDemo.Contracts.Events;

/// <summary>
/// Application event published to RabbitMQ (via Wolverine outbox) when an order is shipped.
/// Consumed by the read-side projector to update the order summary read model.
/// </summary>
public sealed record OrderShippedApplicationEvent(Guid OrderId, DateTimeOffset ShippedAt);
