using OrderDemo.Domain.Aggregates;

namespace OrderDemo.Contracts.Commands;

/// <summary>Command to place a new order.</summary>
public sealed record PlaceOrderCommand(Guid CustomerId, IReadOnlyList<OrderItem> Items);
