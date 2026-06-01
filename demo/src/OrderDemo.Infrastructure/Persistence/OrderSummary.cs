using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderDemo.Infrastructure.Persistence;

/// <summary>
/// Denormalized read model stored in the <c>order_summaries</c> collection.
/// Updated by <see cref="Projectors.OrderSummaryProjector"/> via RabbitMQ events.
/// </summary>
public sealed class OrderSummary
{
    [BsonId]
    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid OrderId { get; set; }

    [BsonGuidRepresentation(GuidRepresentation.Standard)]
    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal DiscountPercent { get; set; }
    public string Status { get; set; } = "Pending";
    public DateTimeOffset PlacedAt { get; set; }
    public DateTimeOffset? ShippedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelReason { get; set; }
}
