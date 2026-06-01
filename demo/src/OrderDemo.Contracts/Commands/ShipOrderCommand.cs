namespace OrderDemo.Contracts.Commands;

/// <summary>Command to mark an existing order as shipped.</summary>
public sealed record ShipOrderCommand(Guid OrderId);
