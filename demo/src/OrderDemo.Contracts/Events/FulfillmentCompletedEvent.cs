namespace OrderDemo.Contracts.Events;

/// <summary>
/// Application event cascaded from OrderFulfillmentSaga when delivery is confirmed.
/// The saga deletion and this outbox entry commit atomically in the same transaction.
/// </summary>
public sealed record FulfillmentCompletedEvent(Guid OrderId, DateTimeOffset DeliveredAt);
