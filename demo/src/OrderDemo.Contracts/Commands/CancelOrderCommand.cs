namespace OrderDemo.Contracts.Commands;

/// <summary>Command to cancel a pending order.</summary>
public sealed record CancelOrderCommand(Guid OrderId, string? Reason = null);
