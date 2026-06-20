using Wolverine.Persistence.Sagas;

namespace OrderDemo.Contracts.Events;

/// <summary>
/// Application event published to RabbitMQ (via Wolverine outbox) when an order is shipped.
/// Consumed by the read-side projector to update the order summary read model.
/// [SagaIdentity] on OrderId allows Wolverine to correlate this event with OrderFulfillmentSaga.
/// </summary>
public sealed record OrderShippedApplicationEvent(
    [property: SagaIdentity] Guid OrderId,
    DateTimeOffset ShippedAt);
