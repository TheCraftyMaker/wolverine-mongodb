namespace OrderDemo.Domain.Aggregates.Events;

/// <summary>Domain event raised when a percentage discount is applied to an order.</summary>
public sealed record DiscountApplied(
    Guid OrderId,
    /// <summary>The incremental discount applied in this operation (e.g. 10 for 10%).</summary>
    decimal DiscountPercent,
    decimal NewTotal,
    /// <summary>The cumulative discount on the order after this application. Used by projectors to assign absolute values and stay idempotent on replay.</summary>
    decimal CumulativeDiscountPercent);
