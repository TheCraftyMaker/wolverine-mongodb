using MongoDB.Driver;
using OrderDemo.Contracts.Events;
using OrderDemo.Infrastructure.Persistence;

namespace OrderDemo.Infrastructure.Projectors;

/// <summary>
/// Projects <c>FulfillmentShippedEvent</c> and <c>FulfillmentCompletedEvent</c> — cascaded from
/// <see cref="OrderDemo.Application.Sagas.OrderFulfillmentSaga"/> — onto the
/// <see cref="FulfillmentDeliveryStatus"/> read model.
///
/// Runs on a durable local queue (configured in Program.cs / OrdersFixture) so the projection
/// survives process crashes between saga commit and handler invocation. Writes are idempotent:
/// <c>$setOnInsert</c> on <see cref="FulfillmentDeliveryStatus.ShippedAt"/> means a late/retried
/// FulfillmentShippedEvent never overwrites a status already advanced to Delivered.
///
/// Uses <see cref="IMongoDatabase"/> directly — not <c>MongoDbUnitOfWork</c> — because the
/// projector runs outside the saga's transaction; it receives the cascade event from the
/// outbox/durable-inbox queue after the saga has already committed.
/// </summary>
[Wolverine.Attributes.WolverineHandler]
public static class FulfillmentStatusProjector
{
    private const string Collection = "fulfillment_delivery_statuses";

    public static async Task Handle(
        FulfillmentShippedEvent evt,
        IMongoDatabase db,
        CancellationToken ct)
    {
        var collection = db.GetCollection<FulfillmentDeliveryStatus>(Collection);
        var filter = Builders<FulfillmentDeliveryStatus>.Filter.Eq(s => s.OrderId, evt.OrderId);
        var update = Builders<FulfillmentDeliveryStatus>.Update
            .SetOnInsert(s => s.OrderId, evt.OrderId)
            .Set(s => s.ShippedAt, evt.ShippedAt)
            .Set(s => s.Status, "Shipped");
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }

    public static async Task Handle(
        FulfillmentCompletedEvent evt,
        IMongoDatabase db,
        CancellationToken ct)
    {
        var collection = db.GetCollection<FulfillmentDeliveryStatus>(Collection);
        var filter = Builders<FulfillmentDeliveryStatus>.Filter.Eq(s => s.OrderId, evt.OrderId);
        var update = Builders<FulfillmentDeliveryStatus>.Update
            .Set(s => s.DeliveredAt, evt.DeliveredAt)
            .Set(s => s.Status, "Delivered");
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, ct);
    }
}
