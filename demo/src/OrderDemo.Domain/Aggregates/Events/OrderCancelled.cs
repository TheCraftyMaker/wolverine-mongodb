namespace OrderDemo.Domain.Aggregates.Events;

/// <summary>Domain event raised when an order is cancelled.</summary>
public sealed record OrderCancelled(Guid OrderId, string Reason, DateTimeOffset CancelledAt);
