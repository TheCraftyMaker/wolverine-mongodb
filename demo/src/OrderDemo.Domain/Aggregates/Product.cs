namespace OrderDemo.Domain.Aggregates;

/// <summary>
/// Represents a product's stock. Inventory writes participate in the same
/// MongoDB transaction as the order — if reservation fails, the entire
/// order placement is rolled back.
/// </summary>
public sealed class Product
{
    private Product() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int AvailableStock { get; private set; }
    public int ReservedStock { get; private set; }

    public static Product Create(Guid id, string name, int availableStock) => new()
    {
        Id = id,
        Name = name,
        AvailableStock = availableStock,
        ReservedStock = 0
    };

    /// <summary>
    /// Reserve <paramref name="quantity"/> units for an order.
    /// Throws if insufficient stock is available.
    /// </summary>
    public void Reserve(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");

        if (AvailableStock - ReservedStock < quantity)
            throw new InvalidOperationException(
                $"Insufficient stock for product '{Name}'. " +
                $"Available: {AvailableStock - ReservedStock}, requested: {quantity}.");

        ReservedStock += quantity;
    }

    /// <summary>Release previously reserved stock (e.g., on order cancellation).</summary>
    public void Release(int quantity)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));

        ReservedStock = Math.Max(0, ReservedStock - quantity);
    }
}
