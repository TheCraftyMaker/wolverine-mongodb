namespace OrderDemo.Domain.Aggregates.Events;

/// <summary>Domain event raised when a percentage discount is applied to an order.</summary>
public sealed record DiscountApplied(
    Guid OrderId,
    decimal DiscountPercent,
    decimal NewTotal,
    DateTimeOffset AppliedAt);
