using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderDemo.Infrastructure.Persistence;

/// <summary>
/// Read model projected from <c>FulfillmentShippedEvent</c> and <c>FulfillmentCompletedEvent</c>
/// (cascaded from <see cref="OrderDemo.Application.Sagas.OrderFulfillmentSaga"/>). Tracks the
/// delivery timeline orthogonally to <see cref="OrderSummary"/> — orthogonal data, no duplication.
/// Stored in the <c>fulfillment_delivery_statuses</c> collection.
/// </summary>
public sealed class FulfillmentDeliveryStatus
{
    [BsonId]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid OrderId { get; set; }

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset ShippedAt { get; set; }

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset? DeliveredAt { get; set; }

    public string Status { get; set; } = "Shipped";
}
