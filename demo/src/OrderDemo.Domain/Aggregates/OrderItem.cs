namespace OrderDemo.Domain.Aggregates;

/// <summary>A single line item inside an order.</summary>
public sealed record OrderItem(Guid ProductId, string ProductName, int Quantity, decimal UnitPrice)
{
    public decimal LineTotal => Quantity * UnitPrice;
}
