namespace OrderDemo.Contracts.Commands;

/// <summary>Command to apply a percentage discount (0 &lt; percent ≤ 50) to a pending order.</summary>
public sealed record ApplyDiscountCommand(Guid OrderId, decimal DiscountPercent);
