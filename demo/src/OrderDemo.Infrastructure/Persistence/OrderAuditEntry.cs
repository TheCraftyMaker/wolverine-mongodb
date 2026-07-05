using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace OrderDemo.Infrastructure.Persistence;

/// <summary>
/// An audit-trail document recording an action taken against an order. Written directly
/// through <see cref="Wolverine.MongoDB.MongoDbUnitOfWork"/> — no repository, no
/// <c>[Entity]</c>/<c>IStorageAction&lt;T&gt;</c> return value — showcasing the UoW as the
/// recommended write surface for handlers that write to arbitrary collections.
///
/// Lives alongside <see cref="OrderSummary"/>/<see cref="OrderNote"/> in Infrastructure
/// (not Domain) because it carries Mongo BSON attributes and the Domain project is
/// intentionally persistence-agnostic.
/// </summary>
public sealed class OrderAuditEntry
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string PerformedBy { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.DateTime)]
    public DateTimeOffset OccurredAt { get; set; }
}
