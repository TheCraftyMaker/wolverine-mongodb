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

    public Task<OrderSummary?> FindByOrderIdAsync(Guid orderId, CancellationToken ct = default)
        => _summaries
            .Find(Builders<OrderSummary>.Filter.Eq(s => s.OrderId, orderId))
            .FirstOrDefaultAsync(ct)!;

    /// <summary>
    /// Full-document replace-or-insert. Use for events that always own the full document
    /// shape (Shipped, Cancelled, Discount). Do NOT use for OrderPlaced — use
    /// <see cref="InsertIfNotExistsAsync"/> instead to avoid overwriting newer state on replay.
    /// </summary>
    public Task UpsertAsync(OrderSummary summary, CancellationToken ct = default)
        => _summaries.ReplaceOneAsync(
            Builders<OrderSummary>.Filter.Eq(s => s.OrderId, summary.OrderId),
            summary,
            new ReplaceOptions { IsUpsert = true },
            ct);

    /// <summary>
    /// Inserts <paramref name="summary"/> only if no document with the same
    /// <see cref="OrderSummary.OrderId"/> exists yet. Uses <c>$setOnInsert</c> so
    /// a retried or late-arriving <c>OrderPlaced</c> event never overwrites a summary
    /// already advanced to a later state.
    /// </summary>
    public Task InsertIfNotExistsAsync(OrderSummary summary, CancellationToken ct = default)
    {
        var filter = Builders<OrderSummary>.Filter.Eq(s => s.OrderId, summary.OrderId);
        var update = Builders<OrderSummary>.Update
            .SetOnInsert(s => s.OrderId, summary.OrderId)
            .SetOnInsert(s => s.CustomerId, summary.CustomerId)
            .SetOnInsert(s => s.TotalAmount, summary.TotalAmount)
            .SetOnInsert(s => s.Status, summary.Status)
            .SetOnInsert(s => s.PlacedAt, summary.PlacedAt);
        return _summaries.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }
}
