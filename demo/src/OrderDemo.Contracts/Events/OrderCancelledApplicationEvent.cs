namespace OrderDemo.Contracts.Events;

/// <summary>
/// Application event published to RabbitMQ (via Wolverine outbox) when an order is cancelled.
/// Consumed by the read-side projector to update the order summary read model.
/// </summary>
public sealed record OrderCancelledApplicationEvent(
    Guid OrderId,
    string Reason,
    DateTimeOffset CancelledAt);
