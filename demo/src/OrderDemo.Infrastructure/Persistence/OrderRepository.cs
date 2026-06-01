using MongoDB.Driver;
using OrderDemo.Domain.Aggregates;

namespace OrderDemo.Infrastructure.Persistence;

/// <summary>
/// MongoDB implementation of <see cref="IOrderRepository"/> (write side).
///
/// All mutating operations accept an <see cref="IClientSessionHandle"/> so that
/// writes participate in the Wolverine-managed MongoDB transaction. Read operations
/// do not need the session and run outside the transaction.
///
/// Wolverine's <c>AutoApplyTransactions()</c> detects the <see cref="IMongoDatabase"/>
/// constructor dependency transitively from the command handlers and injects a managed
/// session frame around them.
/// </summary>
public sealed class OrderRepository(IMongoDatabase database) : IOrderRepository
{
    private readonly IMongoCollection<Order> _orders =
        database.GetCollection<Order>("orders");

    public Task AddAsync(Order order, IClientSessionHandle session, CancellationToken ct = default)
        => _orders.InsertOneAsync(session, order, cancellationToken: ct);

    public Task UpdateAsync(Order order, IClientSessionHandle session, CancellationToken ct = default)
        => _orders.ReplaceOneAsync(
            session,
            Builders<Order>.Filter.Eq(o => o.Id, order.Id),
            order,
            cancellationToken: ct);

    public Task<Order?> FindAsync(Guid id, CancellationToken ct = default)
        => _orders
            .Find(Builders<Order>.Filter.Eq(o => o.Id, id))
            .FirstOrDefaultAsync(ct)!;
}
