using MongoDB.Driver;

namespace OrderDemo.Infrastructure.Persistence;

/// <summary>
/// Read-side repository for <see cref="OrderSummary"/> documents.
/// Plain MongoDB operations — no session needed because the read side
/// is updated idempotently via upserts from <see cref="Projectors.OrderSummaryProjector"/>.
/// </summary>
public sealed class OrderSummaryRepository(IMongoDatabase database)
{
    private readonly IMongoCollection<OrderSummary> _summaries =
        database.GetCollection<OrderSummary>("order_summaries");

    public async Task<IReadOnlyList<OrderSummary>> GetAllAsync(CancellationToken ct = default)
        => await _summaries.Find(Builders<OrderSummary>.Filter.Empty).ToListAsync(ct);

    public Task UpsertAsync(OrderSummary summary, CancellationToken ct = default)
        => _summaries.ReplaceOneAsync(
            Builders<OrderSummary>.Filter.Eq(s => s.OrderId, summary.OrderId),
            summary,
            new ReplaceOptions { IsUpsert = true },
            ct);
}
