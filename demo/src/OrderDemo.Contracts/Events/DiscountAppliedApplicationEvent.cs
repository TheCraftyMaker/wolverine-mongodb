namespace OrderDemo.Contracts.Events;

/// <summary>
/// Application event published to RabbitMQ (via Wolverine outbox) when a discount is applied.
/// Consumed by the read-side projector to update the order total in the read model.
/// </summary>
public sealed record DiscountAppliedApplicationEvent(
    Guid OrderId,
    decimal DiscountPercent,
    decimal NewTotal);
