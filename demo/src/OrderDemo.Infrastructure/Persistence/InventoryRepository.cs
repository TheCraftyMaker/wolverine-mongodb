using MongoDB.Driver;
using OrderDemo.Domain.Aggregates;

namespace OrderDemo.Infrastructure.Persistence;

public sealed class InventoryRepository(IMongoDatabase database) : IInventoryRepository
{
    private readonly IMongoCollection<Product> _products =
        database.GetCollection<Product>("products");

    public Task<Product?> FindByIdAsync(Guid productId, CancellationToken ct = default)
        => _products
            .Find(Builders<Product>.Filter.Eq(p => p.Id, productId))
            .FirstOrDefaultAsync(ct)!;

    public Task UpdateAsync(Product product, IClientSessionHandle session, CancellationToken ct = default)
        => _products.ReplaceOneAsync(
            session,
            Builders<Product>.Filter.Eq(p => p.Id, product.Id),
            product,
            cancellationToken: ct);

    public Task AddAsync(Product product, CancellationToken ct = default)
        => _products.InsertOneAsync(product, cancellationToken: ct);
}
