using MongoDB.Driver;
using OrderDemo.Domain.Aggregates;

namespace OrderDemo.Infrastructure.Persistence;

/// <summary>
/// Repository abstraction for the <see cref="Order"/> aggregate (write side).
/// Mutating operations accept an <see cref="IClientSessionHandle"/> so that
/// domain writes participate in the Wolverine-managed MongoDB transaction.
/// </summary>
public interface IOrderRepository
{
    Task AddAsync(Order order, IClientSessionHandle session, CancellationToken ct = default);
    Task UpdateAsync(Order order, IClientSessionHandle session, CancellationToken ct = default);
    Task<Order?> FindAsync(Guid id, CancellationToken ct = default);
}
