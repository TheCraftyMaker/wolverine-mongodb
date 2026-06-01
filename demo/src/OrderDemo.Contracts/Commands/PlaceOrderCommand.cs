namespace OrderDemo.Contracts.Commands;

/// <summary>A line item in a place-order request.</summary>
public sealed record OrderItemDto(Guid ProductId, string ProductName, int Quantity, decimal UnitPrice);

/// <summary>Command to place a new order.</summary>
public sealed record PlaceOrderCommand(Guid CustomerId, IReadOnlyList<OrderItemDto> Items);
