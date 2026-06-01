using MongoDB.Driver;
using OrderDemo.Domain.Aggregates;

namespace OrderDemo.Infrastructure.Persistence;

/// <summary>
/// Repository abstraction for <see cref="Product"/> inventory (write side).
/// All mutating operations accept an <see cref="IClientSessionHandle"/> so they
/// participate in the same MongoDB transaction as the order write.
/// </summary>
public interface IInventoryRepository
{
    Task<Product?> FindByIdAsync(Guid productId, CancellationToken ct = default);
    Task UpdateAsync(Product product, IClientSessionHandle session, CancellationToken ct = default);
    Task AddAsync(Product product, CancellationToken ct = default);
}
