namespace OrderDemo.Contracts.Events;

/// <summary>
/// Cascaded by OrderFulfillmentSaga when an order is shipped.
/// Demonstrates that saga handlers can publish to the outbox; the
/// ship+saga+outbox entry commit atomically in the same transaction.
/// </summary>
public sealed record FulfillmentShippedEvent(Guid OrderId, DateTimeOffset ShippedAt);
